using FerretSharp.Core.Connections;
using FerretSharp.Core.Workspaces;
using Microsoft.Extensions.Logging;

namespace FerretSharp.UI.State;

/// <summary>
/// What happens to the workspaces and their uncommitted changes (R2, out of <c>Shell.razor</c>): commit and rollback (with
/// the confirmations they need), unlocking and locking, connecting, disconnecting, deleting a connection, closing tabs –
/// and before anything that would drop changes, asking first (<see cref="GuardAsync(string, IReadOnlyList{WorkspaceTabs}, Func{Task})"/>).
/// Owned by the shell and cascaded; the dialogs it asks for are shown by <c>DialogHost</c>. Runs on the UI thread.
/// </summary>
public sealed class WorkspaceLifecycle(
    ShellState shell,
    WorkspaceManager workspaces,
    ActiveConnection active,
    ConnectionManager connections,
    SqlHistoryStore history,
    WorkspaceEditing editing,
    ILogger<WorkspaceLifecycle> logger)
{
    /// <summary>"Neu verbinden" after a loss: the old transactions no longer exist, there is nothing to ask about.</summary>
    private bool _connectionGone;

    public WorkspaceEditing Editing => editing;

    /// <summary>Asking before a rollback of this workspace (it cannot be undone).</summary>
    public WorkspaceTabs? ConfirmRollback { get; private set; }

    /// <summary>Asking before a commit of this workspace on Prod.</summary>
    public WorkspaceTabs? ConfirmCommit { get; private set; }

    /// <summary>Asking before closing a tab with pending changes.</summary>
    public TableTab? ConfirmCloseTab { get; private set; }

    /// <summary>Asking what happens to uncommitted changes before leaving: commit, discard or cancel.</summary>
    public LeaveRequest? PendingLeave { get; private set; }

    /// <summary>"Änderungen als SQL anzeigen": the statements a write would run.</summary>
    public string? PendingSql { get; private set; }

    /// <summary>The active connection is Prod: commits are confirmed, the frame is red.</summary>
    public bool IsProd => active.Profile?.Kind == ConnectionKind.Prod && active.Status is ConnectionStatus.Connected or ConnectionStatus.Connecting;

    public string WorkspaceName(Guid workspaceId) => workspaces.Find(workspaceId)?.Name ?? "Workspace";

    /// <summary>Unlocks the workspace (WP-10) and reloads its tabs: they showed the read-only snapshot.</summary>
    public async Task UnlockAsync(Guid workspaceId)
    {
        shell.CloseUnlock();
        if (!shell.ShowFailure(await shell.RunDbAsync(logger, active.Profile, () => workspaces.UnlockAsync(workspaceId, CancellationToken.None))))
        {
            return;
        }

        foreach (var tab in shell.Workspaces.FirstOrDefault(w => w.WorkspaceId == workspaceId)?.Tabs ?? [])
        {
            shell.RequestTabCommand(tab, TabCommand.Reload);
        }

        shell.Notify($"Workspace „{WorkspaceName(workspaceId)}“ ist zum Schreiben freigeschaltet.");
    }

    /// <summary>Locks the workspace again – after asking what happens to its uncommitted changes.</summary>
    public Task LockAsync(Guid workspaceId) =>
        GuardAsync("Workspace sperren", [workspaceId], async () =>
        {
            if (shell.ShowFailure(await shell.RunDbAsync(logger, active.Profile, () => workspaces.LockAsync(workspaceId, CancellationToken.None))))
            {
                shell.Notify($"Workspace „{WorkspaceName(workspaceId)}“ ist wieder schreibgeschützt.");
            }
        });

    /// <summary>Commits right away, on Prod only after a confirmation.</summary>
    public Task RequestCommitAsync(WorkspaceTabs workspace)
    {
        if (!IsProd)
        {
            return editing.CommitAsync(workspace);
        }

        ConfirmCommit = workspace;
        shell.NotifyChanged(); // also reached from the shortcut, outside a Blazor event
        return Task.CompletedTask;
    }

    public async Task CommitAsync(WorkspaceTabs workspace)
    {
        ConfirmCommit = null;
        await editing.CommitAsync(workspace);
    }

    public void RequestRollback(WorkspaceTabs workspace)
    {
        ConfirmRollback = workspace;
        shell.NotifyChanged();
    }

    public async Task RollbackAsync(WorkspaceTabs workspace)
    {
        ConfirmRollback = null;
        await editing.RollbackAsync(workspace);
    }

    /// <summary>"Abbrechen" in any of the confirmations.</summary>
    public void CancelConfirmation()
    {
        (ConfirmCommit, ConfirmRollback, ConfirmCloseTab) = (null, null, null);
        shell.NotifyChanged();
    }

    public void ShowPendingSql(WorkspaceTabs workspace)
    {
        PendingSql = editing.DescribePending(workspace);
        shell.NotifyChanged();
    }

    public void ClosePendingSql()
    {
        PendingSql = null;
        shell.NotifyChanged();
    }

    /// <summary>Runs <paramref name="then"/> – after asking, if the workspaces have uncommitted changes.</summary>
    public Task GuardAsync(string what, IReadOnlyList<Guid> workspaceIds, Func<Task> then) =>
        GuardAsync(what, shell.Workspaces.Where(w => workspaceIds.Contains(w.WorkspaceId)).ToList(), then);

    /// <summary>Asks before <paramref name="then"/> if workspaces have uncommitted work; runs it right away otherwise.</summary>
    public Task GuardAsync(string what, IReadOnlyList<WorkspaceTabs> scope, Func<Task> then)
    {
        var withWork = scope.Where(w => editing.SummaryOf(w).HasWork).Select(w => w.WorkspaceId).ToList();
        if (withWork.Count == 0 || shell.ConnectionLost is not null || _connectionGone)
        {
            return then();
        }

        PendingLeave = new LeaveRequest(what, withWork, then);
        shell.NotifyChanged();
        return Task.CompletedTask;
    }

    /// <summary>Leave dialog: commit or discard every listed workspace, then continue (only if all succeeded).</summary>
    public async Task LeaveAsync(bool commit)
    {
        if (PendingLeave is not { } leave)
        {
            return;
        }

        foreach (var id in leave.WorkspaceIds)
        {
            if (shell.Workspaces.FirstOrDefault(w => w.WorkspaceId == id) is not { } workspace)
            {
                continue;
            }

            if (!(commit ? await editing.CommitAsync(workspace) : await editing.RollbackAsync(workspace)))
            {
                CancelLeave(); // a write problem dialog (or error) explains why; nothing is left behind half done
                return;
            }
        }

        CancelLeave();
        await leave.Continue();
    }

    public void CancelLeave()
    {
        PendingLeave = null;
        shell.NotifyChanged();
    }

    /// <summary>A tab with pending changes asks first; its flushed changes stay in the workspace's transaction.</summary>
    public void CloseTab(WorkspaceTab tab)
    {
        if (tab is TableTab { Changes.PendingCount: > 0 } table)
        {
            ConfirmCloseTab = table;
            shell.NotifyChanged();
        }
        else
        {
            shell.CloseTab(tab);
        }
    }

    public void CloseTabConfirmed(TableTab tab)
    {
        ConfirmCloseTab = null;
        shell.CloseTab(tab);
    }

    /// <summary>Hands the tabs of every open workspace to the manager, which saves what changed (debounced).</summary>
    public void SaveTabs()
    {
        foreach (var workspace in shell.Workspaces)
        {
            workspaces.UpdateTabs(workspace.WorkspaceId, workspace.Tabs.Select(t => t.ToState(workspace.Tabs)).ToList(), workspace.ActiveIndex);
        }
    }

    /// <summary>Connecting closes the current connection: uncommitted work is confirmed first (not after it was lost).</summary>
    public Task ConnectAsync(ConnectionProfile profile) =>
        GuardAsync($"Verbindung zu {profile.Name} öffnen", shell.Workspaces, () =>
        {
            _connectionGone = false;
            SaveTabs();
            shell.ClearWorkspaces();
            // Off the UI thread; ActiveConnection reports progress through its Changed event and never throws.
            _ = Task.Run(() => active.ConnectAsync(profile, CancellationToken.None));
            return Task.CompletedTask;
        });

    /// <summary>"Neu verbinden" after the connection was lost: its transactions are gone, nothing left to confirm.</summary>
    public void Reconnect(ConnectionProfile profile)
    {
        _connectionGone = true;
        foreach (var workspace in shell.Workspaces)
        {
            editing.Forget(workspace.WorkspaceId);
        }

        shell.Connect(profile);
    }

    public Task DisconnectAsync() => GuardAsync("Verbindung trennen", shell.Workspaces, DisconnectCoreAsync);

    public Task DeleteAsync(ConnectionProfile profile)
    {
        shell.CancelDelete();
        if (active.Profile?.Id != profile.Id)
        {
            return DeleteCoreAsync(profile);
        }

        return GuardAsync("Verbindung löschen", shell.Workspaces, async () =>
        {
            await DisconnectCoreAsync();
            await DeleteCoreAsync(profile);
        });
    }

    /// <summary>Quitting with uncommitted work asks first (the window's closing handler, <see cref="ExitGuard"/>).</summary>
    public bool CanExit => editing.WithWork().Count == 0 || shell.ConnectionLost is not null;

    public Task RequestExitAsync(Action approve) =>
        GuardAsync("FerretSharp beenden", shell.Workspaces, () =>
        {
            approve();
            return Task.CompletedTask;
        });

    private async Task DisconnectCoreAsync()
    {
        SaveTabs();
        shell.ClearConnectionLost();
        await active.DisconnectAsync();
        shell.ClearWorkspaces();
        shell.ShowConnections();
    }

    private async Task DeleteCoreAsync(ConnectionProfile profile)
    {
        await connections.DeleteAsync(profile.Id, CancellationToken.None);
        await workspaces.DeleteForConnectionAsync(profile.Id);
        history.Delete(profile.Id);
    }
}
