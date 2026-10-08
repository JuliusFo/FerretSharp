using FerretSharp.Core.Connections;
using FerretSharp.Core.Workspaces;
using Microsoft.Extensions.Logging;

namespace FerretSharp.UI.State;

/// <summary>
/// What happens to the workspaces and their uncommitted changes (R2, out of <c>Shell.razor</c>): commit and rollback (with
/// the confirmations they need), unlocking and locking, connecting, disconnecting, deleting a connection, closing tabs –
/// and before anything that would drop changes, asking first (<see cref="GuardAsync(string, IReadOnlyList{WorkspaceTabs}, Func{Task})"/>).
/// Owned by the shell and cascaded; the dialogs it asks for are shown by <c>DialogHost</c>. Runs on the UI thread.
/// WP-24: several connections can be open; actions on a workspace go to its connection, the rest to the shown one.
/// </summary>
public sealed class WorkspaceLifecycle(
    ShellState shell,
    ConnectionHub hub,
    ConnectionManager connections,
    SqlHistoryStore history,
    WorkspaceEditing editing,
    ILogger<WorkspaceLifecycle> logger)
{
    /// <summary>The shown connection (an idle one if none is open).</summary>
    private ActiveConnection active => hub.Shown.Active;


    private ConnectionScope ScopeOf(Guid workspaceId) => hub.OwnerOf(workspaceId) ?? hub.Shown;

    /// <summary>The workspace's connection was found lost: its transaction is gone, there is nothing left to ask about.</summary>
    private bool IsLost(Guid workspaceId) => ScopeOf(workspaceId).Lost is not null;

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

    public string WorkspaceName(Guid workspaceId) => ScopeOf(workspaceId).Workspaces.Find(workspaceId)?.Name ?? "Workspace";

    /// <summary>Unlocks the workspace (WP-10) and reloads its tabs: they showed the read-only snapshot.</summary>
    public async Task UnlockAsync(Guid workspaceId)
    {
        shell.CloseUnlock();
        if (!shell.ShowFailure(await shell.RunDbAsync(logger, ScopeOf(workspaceId).Active, () => ScopeOf(workspaceId).Workspaces.UnlockAsync(workspaceId, CancellationToken.None))))
        {
            return;
        }

        shell.ReloadTableTabs(workspaceId);
        shell.Notify($"Workspace „{WorkspaceName(workspaceId)}“ ist zum Schreiben freigeschaltet.");
    }

    /// <summary>
    /// "Session trennen" – a statement does not react to cancelling (<c>RunProgress</c> asked first if writes would be lost):
    /// closes the workspace's session, the statement ends as cancelled. What was written in its transaction is gone, grid
    /// writes become pending again; the next database access opens a new session.
    /// </summary>
    public async Task ResetSessionAsync(Guid workspaceId)
    {
        // The session leaves the workspace at once (and the statement is let go); closing it may take a few seconds more.
        var closing = ScopeOf(workspaceId).Workspaces.ResetSessionAsync(workspaceId);
        if (shell.FindWorkspace(workspaceId) is { } workspace)
        {
            editing.SessionReset(workspace);
        }

        shell.Notify($"Session von „{WorkspaceName(workspaceId)}“ getrennt – beim nächsten Zugriff öffnet sich eine neue.");
        shell.NotifyChanged(); // status bar: the transaction is gone
        await closing;
    }

    /// <summary>Locks the workspace again – after asking what happens to its uncommitted changes.</summary>
    public Task LockAsync(Guid workspaceId) =>
        GuardAsync("Workspace sperren", [workspaceId], async () =>
        {
            if (shell.ShowFailure(await shell.RunDbAsync(logger, ScopeOf(workspaceId).Active, () => ScopeOf(workspaceId).Workspaces.LockAsync(workspaceId, CancellationToken.None))))
            {
                shell.Notify($"Workspace „{WorkspaceName(workspaceId)}“ ist wieder schreibgeschützt.");
            }
        });

    /// <summary>Commits right away, on Prod only after a confirmation.</summary>
    public Task RequestCommitAsync(WorkspaceTabs workspace)
    {
        if (ScopeOf(workspace.WorkspaceId).Profile?.Kind != ConnectionKind.Prod)
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
        GuardAsync(what, shell.AllWorkspaces.Where(w => workspaceIds.Contains(w.WorkspaceId)).ToList(), then);

    /// <summary>
    /// Asks before <paramref name="then"/> if workspaces have uncommitted work; runs it right away otherwise. Workspaces of a
    /// lost connection are left out: their transactions are gone, committing is impossible (each connection on its own, WP-24).
    /// </summary>
    public Task GuardAsync(string what, IReadOnlyList<WorkspaceTabs> scope, Func<Task> then)
    {
        var withWork = scope.Where(w => !IsLost(w.WorkspaceId) && editing.SummaryOf(w).HasWork).Select(w => w.WorkspaceId).ToList();
        if (withWork.Count == 0)
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
            if (shell.FindWorkspace(id) is not { } workspace)
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

    /// <summary>Hands the tabs of every open workspace (all connections) to its manager, which saves what changed (debounced).</summary>
    public void SaveTabs()
    {
        foreach (var workspace in shell.AllWorkspaces)
        {
            ScopeOf(workspace.WorkspaceId).Workspaces.UpdateTabs(workspace.WorkspaceId, workspace.Tabs.Select(t => t.ToState(workspace.Tabs)).ToList(), workspace.ActiveIndex);
        }
    }

    /// <summary>
    /// Shows the connection, opening it if it is not open yet (WP-24): the connection shown so far stays open in the
    /// background with its workspaces and transactions, so there is nothing to ask. A connection found lost in the
    /// background is only shown – with the banner, so the user sees that its uncommitted work is gone – and connected
    /// again through <see cref="Reconnect"/>; a failed one is connected again right away.
    /// </summary>
    public Task ConnectAsync(ConnectionProfile profile)
    {
        SaveTabs();
        if (hub.Find(profile.Id) is { Lost: not null } lost)
        {
            hub.Show(lost);
            return Task.CompletedTask;
        }

        // Showing (and opening the scope) here on the UI thread; only connecting runs in the background – ActiveConnection
        // reports progress through its Changed event and never throws.
        var scope = hub.Show(profile);
        if (ConnectionHub.NeedsConnect(scope))
        {
            _ = Task.Run(() => scope.Active.ConnectAsync(profile, CancellationToken.None));
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// "Neu verbinden" after the connection was lost: its transactions are gone, nothing left to confirm. Its old
    /// sessions and workspace tabs are dropped; the workspaces are restored from what was saved.
    /// </summary>
    public void Reconnect(ConnectionProfile profile)
    {
        if (hub.Find(profile.Id) is not { } scope)
        {
            shell.Connect(profile);
            return;
        }

        foreach (var workspace in WorkspacesOf(scope))
        {
            editing.Forget(workspace.WorkspaceId);
        }

        SaveTabs();
        scope.Active.ClearLost();
        shell.ClearWorkspaces(scope.Id);
        hub.Show(scope);
        shell.ShowExplorer();
        _ = Task.Run(() => scope.Active.ConnectAsync(profile, CancellationToken.None));
    }

    /// <summary>Disconnects the shown connection (after asking about its uncommitted work); another open one is shown.</summary>
    public Task DisconnectAsync() => hub.Current is { } current ? DisconnectAsync(current) : Task.CompletedTask;

    /// <summary>Disconnects an open connection, shown or not, after asking about its uncommitted work.</summary>
    public Task DisconnectAsync(ConnectionScope scope) =>
        GuardAsync($"Verbindung {scope.Profile?.Name} trennen", WorkspacesOf(scope), () => DisconnectCoreAsync(scope));

    public Task DeleteAsync(ConnectionProfile profile)
    {
        shell.CancelDelete();
        if (hub.Find(profile.Id) is not { } scope)
        {
            return DeleteCoreAsync(profile);
        }

        return GuardAsync("Verbindung löschen", WorkspacesOf(scope), async () =>
        {
            await DisconnectCoreAsync(scope);
            await DeleteCoreAsync(profile);
        });
    }

    /// <summary>Quitting with uncommitted work in any open connection asks first (the window's closing handler, <see cref="ExitGuard"/>).</summary>
    public bool CanExit => editing.WithWork().All(w => IsLost(w.WorkspaceId));

    public Task RequestExitAsync(Action approve) =>
        GuardAsync("FerretSharp beenden", shell.AllWorkspaces, () =>
        {
            approve();
            return Task.CompletedTask;
        });

    private IReadOnlyList<WorkspaceTabs> WorkspacesOf(ConnectionScope scope) => shell.AllWorkspaces.Where(w => w.ConnectionId == scope.Id).ToList();

    private async Task DisconnectCoreAsync(ConnectionScope scope)
    {
        SaveTabs();
        foreach (var workspace in WorkspacesOf(scope))
        {
            editing.Forget(workspace.WorkspaceId);
        }

        await hub.CloseAsync(scope);
        shell.ClearWorkspaces(scope.Id);
        if (hub.Current is null)
        {
            shell.ShowConnections();
        }
    }

    private async Task DeleteCoreAsync(ConnectionProfile profile)
    {
        await connections.DeleteAsync(profile.Id, CancellationToken.None);
        await hub.Shown.Workspaces.DeleteForConnectionAsync(profile.Id);
        history.Delete(profile.Id);
    }
}
