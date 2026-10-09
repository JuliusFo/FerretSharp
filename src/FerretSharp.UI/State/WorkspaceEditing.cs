using FerretSharp.Core.Connections;
using FerretSharp.Core.Data;
using FerretSharp.Core.Query;
using FerretSharp.Core.Resources;
using FerretSharp.Core.Settings;
using FerretSharp.Core.Workspaces;
using FerretSharp.UI.Resources;
using Microsoft.Extensions.Logging;

namespace FerretSharp.UI.State;

/// <summary>Counts for the status bar of one workspace.</summary>
/// <param name="Transaction">The workspace session's transaction; null while the session is not open.</param>
/// <param name="Actions">Uncommitted writes – grid flushes and SQL/LINQ statements –, oldest first.</param>
public sealed record EditSummary(int Pending, int Flushed, TransactionInfo? Transaction, IReadOnlyList<WriteAction> Actions)
{
    /// <summary>Writes taken back that can be applied again (WP-30), the next one first.</summary>
    public IReadOnlyList<WriteAction> Undone { get; init; } = [];

    public bool HasWork => Pending > 0 || Flushed > 0 || Transaction?.Mode == TransactionMode.ReadWrite;

    public bool CanUndo => Actions.Count > 0;

    public bool CanRedo => Undone.Count > 0;
}

/// <summary>What became of a statement's write in the workspace's transaction (SQL editor, LINQ console).</summary>
public enum WriteFate
{
    /// <summary>Still uncommitted in the transaction.</summary>
    Open,

    /// <summary>Taken back with ↶; the transaction is still open.</summary>
    Undone,

    /// <summary>The transaction ended since: commit or rollback.</summary>
    Ended,
}

/// <summary>A write stopped; shown as a dialog until the user decides.</summary>
/// <param name="DuringCommit">Commit continues once the problem is resolved.</param>
/// <param name="Holders">Sessions locking the table (lock conflict); null if unknown (no rights on the V$ views).</param>
public sealed record FlushProblem(Guid WorkspaceId, TableTab Tab, FlushException Error, bool DuringCommit, IReadOnlyList<LockHolder>? Holders);

public enum ProblemChoice
{
    /// <summary>Try again (lock conflict: the other session may be done).</summary>
    Retry,

    /// <summary>Concurrency conflict: write anyway.</summary>
    Overwrite,

    /// <summary>Drop the pending change of the affected row and write the rest.</summary>
    Discard,

    Cancel,
}

/// <summary>
/// Writing for the open workspaces (v2, WP-09): the transaction belongs to the workspace's session, the changes to
/// its tabs. Ctrl+S writes the pending changes of all tabs (one savepoint per tab), commit writes what is pending
/// and commits, rollback discards everything, undo takes back writes – of the grid or a statement, since they
/// share the transaction (<see cref="IDataEditor.Actions"/>) – up to a chosen one, and redo applies them again (WP-30).
/// Owned by the shell.
/// </summary>
public sealed class WorkspaceEditing(
    ShellState shell, ConnectionHub hub, AppSettingsService settings, ILogger<WorkspaceEditing> logger)
{
    /// <summary>The connection a workspace belongs to (WP-24: several can be open; a background one may still finish a write).</summary>
    private ConnectionScope ScopeOf(Guid workspaceId) => hub.ScopeOfWorkspace(workspaceId);

    private WorkspaceManager WorkspacesOf(Guid workspaceId) => ScopeOf(workspaceId).Workspaces;

    /// <summary>Per workspace: the grid writes with what to restore when undone, in the order written.</summary>
    private readonly Dictionary<Guid, List<(Guid ActionId, TableTab Tab, FlushBatch Batch)>> _batches = [];

    /// <summary>Per workspace: the grid writes taken back, by action id – their changes are pending again until redo (WP-30).</summary>
    private readonly Dictionary<Guid, Dictionary<Guid, (TableTab Tab, FlushBatch Batch)>> _undone = [];

    /// <summary>Per workspace: the tab a statement ran in (SQL editor, LINQ console), by the first write of its line.</summary>
    private readonly Dictionary<Guid, Dictionary<Guid, WorkspaceTab>> _statementTabs = [];

    private readonly Dictionary<Guid, HashSet<Guid>> _overwrite = [];

    /// <summary>Workspaces with a write, commit or rollback running (their buttons are disabled).</summary>
    private readonly HashSet<Guid> _busy = [];

    /// <summary>
    /// A write, commit or rollback of this workspace is running. Per workspace (WP-24): a slow commit on one connection must
    /// not silently swallow Ctrl+S in another.
    /// </summary>
    public bool IsBusy(Guid workspaceId) => _busy.Contains(workspaceId);

    public FlushProblem? Problem { get; private set; }

    public static IEnumerable<ChangeTracker> Trackers(WorkspaceTabs workspace) =>
        workspace.TableTabs.Select(t => t.Changes).OfType<ChangeTracker>();

    public EditSummary SummaryOf(WorkspaceTabs workspace) => new(
        Trackers(workspace).Sum(t => t.PendingCount),
        Trackers(workspace).Sum(t => t.FlushedCount),
        hub.ScopeOf(workspace).Workspaces.TransactionOf(workspace.WorkspaceId),
        hub.ScopeOf(workspace).Workspaces.ActionsOf(workspace.WorkspaceId))
    {
        Undone = hub.ScopeOf(workspace).Workspaces.UndoneOf(workspace.WorkspaceId),
    };

    /// <summary>
    /// What became of a statement's write (<paramref name="action"/>, run in the transaction that began at
    /// <paramref name="transactionStart"/>). A redo of it counts as the write itself (<see cref="WriteAction.Origin"/>).
    /// </summary>
    public static WriteFate FateOf(WorkspaceManager workspaces, Guid workspaceId, Guid action, DateTimeOffset? transactionStart)
    {
        if (workspaces.ActionsOf(workspaceId).Any(a => a.Origin == action))
        {
            return WriteFate.Open;
        }

        return workspaces.TransactionOf(workspaceId) is { Mode: TransactionMode.ReadWrite, StartedAt: var started } && started == transactionStart
            ? WriteFate.Undone
            : WriteFate.Ended;
    }

    /// <summary>Workspaces whose changes would be lost (pending, flushed or an open writing transaction).</summary>
    /// <summary>Workspaces with uncommitted work, of all open connections (quitting asks for all).</summary>
    public IReadOnlyList<WorkspaceTabs> WithWork() => shell.AllWorkspaces.Where(w => SummaryOf(w).HasWork).ToList();

    /// <summary>Writes the pending changes of all tabs; false if a problem stopped it (see <see cref="Problem"/>).</summary>
    public Task<bool> FlushAsync(WorkspaceTabs workspace) =>
        StoppedByForm(workspace) ? Task.FromResult(false) : RunAsync(workspace, () => FlushCoreAsync(workspace, duringCommit: false));

    /// <summary>Writes what is pending, then commits; false if a write problem stopped it.</summary>
    public Task<bool> CommitAsync(WorkspaceTabs workspace) => StoppedByForm(workspace) ? Task.FromResult(false) : RunAsync(workspace, async () =>
    {
        if (!await FlushCoreAsync(workspace, duringCommit: true))
        {
            return false;
        }

        var editor = await WorkspacesOf(workspace.WorkspaceId).GetEditorAsync(workspace.WorkspaceId, CancellationToken.None);
        if (editor.Transaction.Mode == TransactionMode.ReadWrite)
        {
            try
            {
                await editor.CommitAsync(CancellationToken.None);
            }
            catch (DatabaseException) when (editor.Transaction.Mode != TransactionMode.ReadWrite)
            {
                // Oracle rolled the transaction back (ORA-02091, deferred constraint) or the session is gone: nothing
                // written is in the database – it becomes pending again instead of vanishing from the grid.
                RestorePending(workspace);
                throw;
            }
        }

        Finish(workspace);
        shell.Notify(GridText.Editing_Committed);
        return true;
    });

    /// <summary>Discards everything: flushed changes (rollback) and pending ones.</summary>
    public Task<bool> RollbackAsync(WorkspaceTabs workspace) => RunAsync(workspace, async () =>
    {
        if (WorkspacesOf(workspace.WorkspaceId).TransactionOf(workspace.WorkspaceId) is { Mode: TransactionMode.ReadWrite })
        {
            var editor = await WorkspacesOf(workspace.WorkspaceId).GetEditorAsync(workspace.WorkspaceId, CancellationToken.None);
            await editor.RollbackAsync(CancellationToken.None);
        }

        Finish(workspace);
        shell.Notify(GridText.Editing_RolledBack);
        return true;
    });

    /// <summary>
    /// Takes back the last write: rollback to its savepoint. A grid write's changes become pending again; a statement's
    /// rows are as before it (the tabs reload, its result says "undone").
    /// </summary>
    /// <param name="expected">The action the ↶ button named; refused if another one was written meanwhile.</param>
    public Task<bool> UndoLastAsync(WorkspaceTabs workspace, Guid? expected = null) =>
        WorkspacesOf(workspace.WorkspaceId).ActionsOf(workspace.WorkspaceId) is [.., var last]
            ? UndoToAsync(workspace, last.Id, expected ?? last.Id)
            : Task.FromResult(false);

    /// <summary>
    /// Takes back <paramref name="actionId"/> and every later write (WP-30): rollback to its savepoint. Grid writes become
    /// pending again and can be redone; the rows of statements are as before them (the tabs reload, their results say
    /// "undone").
    /// </summary>
    /// <param name="newest">The newest action the user saw; refused if another one was written meanwhile.</param>
    public Task<bool> UndoToAsync(WorkspaceTabs workspace, Guid actionId, Guid newest) => RunAsync(workspace, async () =>
    {
        var id = workspace.WorkspaceId;
        var editor = await WorkspacesOf(id).GetEditorAsync(id, CancellationToken.None);
        var undone = await editor.UndoToAsync(actionId, newest, CancellationToken.None);
        if (undone.Count == 0)
        {
            return false;
        }

        ForgetEndedRedos(id);
        var batches = Batches(id);
        var statements = false;
        foreach (var action in undone) // newest first, like the rollback
        {
            if (batches.FindLastIndex(b => b.ActionId == action.Id) is >= 0 and var index)
            {
                var (_, tab, batch) = batches[index];
                batches.RemoveAt(index);
                tab.Changes?.UndoFlush(batch);
                Undone(id)[action.Id] = (tab, batch);
                shell.RequestTabCommand(tab, TabCommand.Reload);
            }
            else
            {
                statements = true;
            }
        }

        if (statements)
        {
            shell.ReloadTableTabs(id); // any table tab may show rows a statement had changed
        }

        shell.Notify(undone switch
        {
            [{ Kind: WriteActionKind.Grid } single] => TextFormat.Format(GridText.Editing_UndoneGrid, single.Display),
            [var single] => TextFormat.Format(GridText.Editing_Undone, single.Display),
            _ => TextFormat.Format(GridText.Editing_UndoneMany, undone.Count),
        });
        return true;
    });

    /// <summary>Why the next redo cannot run (a grid write whose rows were edited since, or whose tab is closed); null if it can.</summary>
    public string? RedoBlocker(WorkspaceTabs workspace)
    {
        if (WorkspacesOf(workspace.WorkspaceId).UndoneOf(workspace.WorkspaceId) is not [{ Kind: WriteActionKind.Grid } next, ..])
        {
            return null;
        }

        if (Undone(workspace.WorkspaceId).GetValueOrDefault(next.Id) is not { Tab: { } tab, Batch: { } batch }
            || !workspace.TableTabs.Contains(tab) || tab.Changes is not { } tracker)
        {
            return GridText.Editing_RedoTabClosed;
        }

        return tracker.IsPendingAsUndone(batch) ? null : TextFormat.Format(GridText.Editing_RedoEditedSince, tab.Table.DisplayName);
    }

    /// <summary>
    /// Applies a grid write taken back again (WP-30): writes exactly its pending changes, with the usual lock and concurrency
    /// check (a conflict shows the write problem dialog). Statements are redone through <see cref="RedoStatementAsync"/>,
    /// after the user confirmed.
    /// </summary>
    /// <param name="expected">The write the user chose to redo; refused if it is no longer the next one.</param>
    public Task<bool> RedoGridAsync(WorkspaceTabs workspace, Guid expected) => RunAsync(workspace, async () =>
    {
        var id = workspace.WorkspaceId;
        if (RedoBlocker(workspace) is { } blocker)
        {
            throw new RefusedException(blocker);
        }

        if (WorkspacesOf(id).UndoneOf(id) is not [{ Kind: WriteActionKind.Grid } next, ..] || next.Id != expected
            || !Undone(id).Remove(next.Id, out var undone))
        {
            throw new RefusedException(DataText.RedoNotPossible);
        }

        var editor = await WorkspacesOf(id).GetEditorAsync(id, CancellationToken.None);
        var tracker = undone.Tab.Changes!;
        var options = new FlushOptions(settings.Current.LockWaitSeconds, Overwrites(id), RedoOf: next.Id);
        if (!await FlushTabAsync(workspace, editor, undone.Tab, tracker.PendingOperations(undone.Batch), options, duringCommit: false))
        {
            Undone(id)[next.Id] = undone; // still taken back: the problem dialog decides
            return false;
        }

        Overwrites(id).Clear();
        shell.Notify(TextFormat.Format(GridText.Editing_Redone, next.Display));
        return true;
    });

    /// <summary>
    /// Runs a statement taken back again (WP-30), with the same bind values – the caller asked the user first. The data may
    /// have changed meanwhile: the result's <see cref="WriteAction.Rows"/> may differ from the first run's.
    /// </summary>
    /// <returns>The new write; null if it did not run (the failure was shown).</returns>
    public async Task<WriteAction?> RedoStatementAsync(WorkspaceTabs workspace, Guid expected)
    {
        WriteAction? again = null;
        await RunAsync(workspace, async () =>
        {
            var id = workspace.WorkspaceId;
            var editor = await WorkspacesOf(id).GetEditorAsync(id, CancellationToken.None);
            again = await editor.RedoAsync(expected, CancellationToken.None);
            shell.ReloadTableTabs(id); // they may show the rows it changed
            return true;
        });
        return again;
    }

    /// <summary>A statement of the SQL editor or the LINQ console was written: the change overview names its tab and jumps there.</summary>
    public void NoteStatement(WorkspaceTab tab, WriteAction action) => StatementTabs(tab.WorkspaceId)[action.Origin] = tab;

    /// <summary>
    /// Where a write came from (change overview): the table tab of a grid write, the SQL or LINQ tab of a statement; null
    /// if that tab is closed or unknown.
    /// </summary>
    public WorkspaceTab? SourceOf(WorkspaceTabs workspace, WriteAction action)
    {
        WorkspaceTab? tab = action.Kind == WriteActionKind.Grid
            ? GridWriteOf(workspace.WorkspaceId, action.Id)?.Tab
            : _statementTabs.GetValueOrDefault(workspace.WorkspaceId)?.GetValueOrDefault(action.Origin);
        return tab is not null && workspace.Tabs.Contains(tab) ? tab : null;
    }

    /// <summary>The tab and rows of a grid write, written or taken back; null if unknown.</summary>
    public (TableTab Tab, FlushBatch Batch)? GridWriteOf(Guid workspaceId, Guid actionId)
    {
        if (_batches.GetValueOrDefault(workspaceId)?.FindLast(b => b.ActionId == actionId) is { Tab: { } tab, Batch: { } batch })
        {
            return (tab, batch);
        }

        return _undone.GetValueOrDefault(workspaceId)?.TryGetValue(actionId, out var undone) == true ? undone : null;
    }

    /// <summary>The statements a write would run, with values (for "Show changes as SQL").</summary>
    public string DescribePending(WorkspaceTabs workspace)
    {
        var wait = settings.Current.LockWaitSeconds;
        var parts = new List<string>();
        foreach (var tab in workspace.TableTabs.Where(t => t.Changes?.PendingCount > 0))
        {
            var tracker = tab.Changes!;
            parts.Add($"-- {tab.Table.Owner}.{tab.Table.Name}");
            parts.AddRange(DmlBuilder.Describe(tracker.Table, tracker.PendingOperations(), wait).Select(s => BindValues.Describe(s, mask: false) + ";"));
        }

        return parts.Count == 0 ? GridText.Editing_NoPending : string.Join(Environment.NewLine + Environment.NewLine, parts);
    }

    /// <summary>The user decided about <see cref="Problem"/>; writing (and committing) continues where useful.</summary>
    public async Task ResolveAsync(ProblemChoice choice)
    {
        if (Problem is not { } problem)
        {
            return;
        }

        Problem = null;
        shell.NotifyChanged();
        var workspace = shell.FindWorkspace(problem.WorkspaceId);
        if (workspace is null || choice == ProblemChoice.Cancel)
        {
            return;
        }

        switch (choice)
        {
            case ProblemChoice.Overwrite:
                Overwrites(problem.WorkspaceId).Add(problem.Error.Operation.Change.Id);
                break;
            case ProblemChoice.Discard when problem.Error.Operation.Change is { IsNew: true, IsInserted: false } added:
                problem.Tab.Changes?.Delete(added);
                shell.RequestTabCommand(problem.Tab, TabCommand.Reload);
                break;
            case ProblemChoice.Discard:
                problem.Tab.Changes?.Revert(problem.Error.Operation.Change.Key);
                shell.RequestTabCommand(problem.Tab, TabCommand.Reload);
                break;
        }

        _ = problem.DuringCommit ? await CommitAsync(workspace) : await FlushAsync(workspace);
    }

    /// <summary>After the connection was lost or closed: nothing of it can be committed any more.</summary>
    /// <summary>
    /// The workspace's session was given up (<see cref="WorkspaceLifecycle.ResetSessionAsync"/>) and its transaction with it:
    /// grid writes become pending again (as after a failed commit), every table tab reloads.
    /// </summary>
    public void SessionReset(WorkspaceTabs workspace)
    {
        RestorePending(workspace);
        Forget(workspace.WorkspaceId);
        shell.ReloadTableTabs(workspace.WorkspaceId);
    }

    public void Forget(Guid workspaceId)
    {
        _batches.Remove(workspaceId);
        _undone.Remove(workspaceId);
        _statementTabs.Remove(workspaceId);
        _overwrite.Remove(workspaceId);
        if (Problem?.WorkspaceId == workspaceId)
        {
            Problem = null;
        }
    }

    private async Task<bool> FlushCoreAsync(WorkspaceTabs workspace, bool duringCommit)
    {
        var tabs = workspace.TableTabs.Where(t => t.Changes?.PendingCount > 0).ToList();
        if (tabs.Count == 0)
        {
            return true;
        }

        var editor = await WorkspacesOf(workspace.WorkspaceId).GetEditorAsync(workspace.WorkspaceId, CancellationToken.None);
        var options = new FlushOptions(settings.Current.LockWaitSeconds, Overwrites(workspace.WorkspaceId));
        foreach (var tab in tabs)
        {
            if (!await FlushTabAsync(workspace, editor, tab, tab.Changes!.PendingOperations(), options, duringCommit))
            {
                return false;
            }
        }

        Overwrites(workspace.WorkspaceId).Clear();
        return true;
    }

    /// <summary>Writes operations of one tab; false if a problem stopped it (see <see cref="Problem"/>).</summary>
    private async Task<bool> FlushTabAsync(
        WorkspaceTabs workspace, IDataEditor editor, TableTab tab, IReadOnlyList<PendingOperation> operations, FlushOptions options, bool duringCommit)
    {
        var tracker = tab.Changes!;
        try
        {
            var result = await editor.FlushAsync(tracker.Table, operations, options, CancellationToken.None);
            var batch = tracker.MarkFlushed(operations, result.NewKeys);
            if (result.Action is { } action)
            {
                Batches(workspace.WorkspaceId).Add((action.Id, tab, batch));
            }

            return true;
        }
        catch (FlushException ex)
        {
            IReadOnlyList<LockHolder>? holders = null;
            if (ex is LockConflictException && ScopeOf(workspace.WorkspaceId).Active.Schema is { } schema)
            {
                holders = await TryAsync(() => schema.GetLockHoldersAsync(tracker.Table.Table.Ref, CancellationToken.None));
            }

            Problem = new FlushProblem(workspace.WorkspaceId, tab, ex, duringCommit, holders);
            return false;
        }
        finally
        {
            shell.RequestTabCommand(tab, TabCommand.Reload);
        }
    }

    /// <summary>A form holds a typed value that cannot be taken (WP-21): nothing is written, the field shows why.</summary>
    private bool StoppedByForm(WorkspaceTabs workspace)
    {
        if (shell.PrepareWrite(workspace.WorkspaceId) is not { } reason)
        {
            return false;
        }

        shell.Notify(reason);
        return true;
    }

    /// <summary>The transaction ended without commit: every write is taken back, newest first, like undo.</summary>
    private void RestorePending(WorkspaceTabs workspace)
    {
        if (!_batches.Remove(workspace.WorkspaceId, out var batches))
        {
            return;
        }

        foreach (var (_, tab, batch) in Enumerable.Reverse(batches))
        {
            tab.Changes?.UndoFlush(batch);
            shell.RequestTabCommand(tab, TabCommand.Reload);
        }
    }

    private void Finish(WorkspaceTabs workspace)
    {
        foreach (var tab in workspace.TableTabs.Where(t => t.Changes is not null))
        {
            tab.Changes!.Clear();
            shell.RequestTabCommand(tab, TabCommand.Reload);
        }

        Forget(workspace.WorkspaceId);
    }

    private async Task<bool> RunAsync(WorkspaceTabs workspace, Func<Task<bool>> action)
    {
        if (!_busy.Add(workspace.WorkspaceId))
        {
            return false;
        }

        shell.NotifyChanged();
        try
        {
            var result = await shell.CallDbAsync(logger, ScopeOf(workspace.WorkspaceId).Active, action);
            return shell.ShowFailure(result) && result.Value;
        }
        finally
        {
            _busy.Remove(workspace.WorkspaceId);
            shell.NotifyChanged();
        }
    }

    private static async Task<T?> TryAsync<T>(Func<Task<T?>> action) where T : class
    {
        try
        {
            return await action();
        }
        catch (DatabaseException)
        {
            return null;
        }
    }

    private List<(Guid ActionId, TableTab Tab, FlushBatch Batch)> Batches(Guid workspaceId) =>
        _batches.TryGetValue(workspaceId, out var list) ? list : _batches[workspaceId] = [];

    private Dictionary<Guid, (TableTab Tab, FlushBatch Batch)> Undone(Guid workspaceId) =>
        _undone.TryGetValue(workspaceId, out var undone) ? undone : _undone[workspaceId] = [];

    private Dictionary<Guid, WorkspaceTab> StatementTabs(Guid workspaceId) =>
        _statementTabs.TryGetValue(workspaceId, out var tabs) ? tabs : _statementTabs[workspaceId] = [];

    /// <summary>Grid writes taken back that a new write made unredoable: their changes simply stay pending.</summary>
    private void ForgetEndedRedos(Guid workspaceId)
    {
        if (_undone.TryGetValue(workspaceId, out var undone))
        {
            var open = WorkspacesOf(workspaceId).UndoneOf(workspaceId).Select(a => a.Id).ToHashSet();
            foreach (var id in undone.Keys.Where(id => !open.Contains(id)).ToList())
            {
                undone.Remove(id);
            }
        }
    }

    private HashSet<Guid> Overwrites(Guid workspaceId) =>
        _overwrite.TryGetValue(workspaceId, out var set) ? set : _overwrite[workspaceId] = [];
}
