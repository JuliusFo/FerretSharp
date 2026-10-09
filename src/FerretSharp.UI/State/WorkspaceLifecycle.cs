using FerretSharp.Core.Connections;
using FerretSharp.Core.Data;
using FerretSharp.Core.Resources;
using FerretSharp.Core.Workspaces;
using FerretSharp.UI.Resources;
using Microsoft.Extensions.Logging;

namespace FerretSharp.UI.State;

/// <summary>
/// What happens to the workspaces and their uncommitted changes (R2, out of <c>Shell.razor</c>): commit and rollback (with
/// the confirmations they need), unlocking and locking, connecting, disconnecting, deleting a connection, closing tabs –
/// and before anything that would drop changes, asking first (<see cref="GuardAsync(string, IReadOnlyList{WorkspaceTabs}, Func{Task})"/>).
/// Owned by the shell and cascaded; the dialogs it asks for are shown by <c>DialogHost</c>. Runs on the UI thread.
/// WP-24: several connections can be open; actions on a workspace go to its connection, the rest to the shown one.
/// </summary>
public sealed partial class WorkspaceLifecycle(
    ShellState shell,
    ConnectionHub hub,
    ConnectionManager connections,
    SqlHistoryStore history,
    WorkspaceEditing editing,
    ILogger<WorkspaceLifecycle> logger)
{
    /// <summary>The shown connection (an idle one if none is open).</summary>
    private ActiveConnection active => hub.Shown.Active;


    private ConnectionScope ScopeOf(Guid workspaceId) => hub.ScopeOfWorkspace(workspaceId);

    /// <summary>The workspace's connection was found lost: its transaction is gone, there is nothing left to ask about.</summary>
    private bool IsLost(Guid workspaceId) => ScopeOf(workspaceId).Lost is not null;

    public WorkspaceEditing Editing => editing;

    /// <summary>Asking before a rollback of this workspace (it cannot be undone).</summary>
    public WorkspaceTabs? ConfirmRollback { get; private set; }

    /// <summary>Asking before a commit of this workspace on Prod.</summary>
    public WorkspaceTabs? ConfirmCommit { get; private set; }

    /// <summary>Asking before closing a tab that would lose something (<see cref="CloseTabQuestion"/>).</summary>
    public CloseTabQuestion? ConfirmCloseTab { get; private set; }

    /// <summary>Asking what happens to uncommitted changes before leaving: commit, discard or cancel.</summary>
    public LeaveRequest? PendingLeave { get; private set; }

    /// <summary>"Show pending changes as SQL": the statements a write would run.</summary>
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
        shell.Notify(TextFormat.Format(ShellText.Lifecycle_Unlocked, WorkspaceName(workspaceId)));
    }

    /// <summary>
    /// "Disconnect session" – a statement does not react to cancelling (<c>RunProgress</c> asked first if writes would be lost):
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

        shell.Notify(TextFormat.Format(ShellText.Lifecycle_SessionReset, WorkspaceName(workspaceId)));
        shell.NotifyChanged(); // status bar: the transaction is gone
        await closing;
    }

    /// <summary>Locks the workspace again – after asking what happens to its uncommitted changes.</summary>
    public Task LockAsync(Guid workspaceId) =>
        GuardAsync(ShellText.Lifecycle_LockWhat, [workspaceId], async () =>
        {
            if (shell.ShowFailure(await shell.RunDbAsync(logger, ScopeOf(workspaceId).Active, () => ScopeOf(workspaceId).Workspaces.LockAsync(workspaceId, CancellationToken.None))))
            {
                shell.Notify(TextFormat.Format(ShellText.Lifecycle_Locked, WorkspaceName(workspaceId)));
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

    /// <summary>"Cancel" in any of the confirmations.</summary>
    public void CancelConfirmation()
    {
        (ConfirmCommit, ConfirmRollback, ConfirmCloseTab, ConfirmRedo) = (null, null, null, null);
        shell.NotifyChanged();
    }

    /// <summary>Asking before a statement taken back runs again (WP-30): it runs on the data as it is now.</summary>
    public (WorkspaceTabs Workspace, WriteAction Action)? ConfirmRedo { get; private set; }

    /// <summary>A statement run again changed another number of rows than the first time: keep it or undo it.</summary>
    public (WorkspaceTabs Workspace, WriteAction First, WriteAction Again)? RedoRowsDiffer { get; private set; }

    /// <summary>
    /// Redo of the next write taken back (WP-30): a grid write is written again at once (the usual lock and conflict
    /// check protect it); a statement only after the user confirmed.
    /// </summary>
    public Task RequestRedo(WorkspaceTabs workspace, WriteAction action)
    {
        if (action.Kind == WriteActionKind.Grid)
        {
            return editing.RedoGridAsync(workspace, action.Id);
        }

        ConfirmRedo = (workspace, action);
        shell.NotifyChanged();
        return Task.CompletedTask;
    }

    /// <summary>The user confirmed: the statement runs again; another row count than the first time is asked about.</summary>
    public async Task RedoConfirmedAsync()
    {
        if (ConfirmRedo is not ({ } workspace, { } action))
        {
            return;
        }

        ConfirmRedo = null;
        shell.NotifyChanged();
        if (await editing.RedoStatementAsync(workspace, action.Id) is not { } again)
        {
            return;
        }

        if (again.Rows != action.Rows)
        {
            RedoRowsDiffer = (workspace, action, again);
            shell.NotifyChanged();
        }
        else
        {
            shell.Notify(TextFormat.Format(GridText.Editing_Redone, again.Display));
        }
    }

    /// <summary>The answer about the other row count: keep the statement's result or undo it again (it stays redoable).</summary>
    public async Task ResolveRedoRowsAsync(bool keep)
    {
        if (RedoRowsDiffer is not ({ } workspace, _, { } again))
        {
            return;
        }

        RedoRowsDiffer = null;
        shell.NotifyChanged();
        if (!keep)
        {
            await editing.UndoToAsync(workspace, again.Id, again.Id);
        }
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

    /// <summary>
    /// A tab that would lose something asks first: pending changes, a value typed but not confirmed (in a grid cell being
    /// edited – <paramref name="cellEditing"/>, from the shortcut – or in the form) or the text of a SQL or LINQ tab.
    /// Flushed changes stay in the workspace's transaction.
    /// </summary>
    public void CloseTab(WorkspaceTab tab, bool cellEditing = false)
    {
        if (CloseTabQuestion.For(tab, cellEditing || shell.HasTypedValues(tab)) is { } question)
        {
            ConfirmCloseTab = question;
            shell.NotifyChanged();
        }
        else
        {
            shell.CloseTab(tab);
        }
    }

    public void CloseTabConfirmed(WorkspaceTab tab)
    {
        ConfirmCloseTab = null;
        shell.CloseTab(tab);
    }

    /// <summary>Hands the tabs of every open workspace (all connections) to its manager, which saves what changed (debounced).</summary>
    public void SaveTabs()
    {
        foreach (var workspace in shell.AllWorkspaces)
        {
            hub.ScopeOf(workspace).Workspaces.UpdateTabs(workspace.WorkspaceId, workspace.Tabs.Select(t => t.ToState(workspace.Tabs)).ToList(), workspace.ActiveIndex);
        }
    }
}
