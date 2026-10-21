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
public sealed record FlushProblem(Guid WorkspaceId, TableTab Tab, FlushException Error, bool DuringCommit, IReadOnlyList<LockHolder>? Holders)
{
    /// <summary>The grid write a redo applied again: resolving continues that redo, not a write of everything pending.</summary>
    public Guid? RedoOf { get; init; }
}

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
public sealed partial class WorkspaceEditing(
    ShellState shell, ConnectionHub hub, AppSettingsService settings, ILogger<WorkspaceEditing> logger, TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>The connection a workspace belongs to (WP-24: several can be open; a background one may still finish a write).</summary>
    private ConnectionScope ScopeOf(Guid workspaceId) => hub.ScopeOfWorkspace(workspaceId);

    private WorkspaceManager WorkspacesOf(Guid workspaceId) => ScopeOf(workspaceId).Workspaces;

    /// <summary>Per workspace: the grid writes with what to restore, those taken back, and the tabs statements ran in (R4b).</summary>
    public GridWriteHistory History { get; } = new();

    private readonly Dictionary<Guid, HashSet<Guid>> _overwrite = [];

    /// <summary>A write, commit or rollback (<see cref="RunAsync"/>) or a writing statement running on a workspace's session.</summary>
    private sealed record Work(DateTimeOffset Started, bool Statement);

    /// <summary>
    /// What runs on each workspace's session. Giving up the session (<see cref="SessionReset"/>) drops its entries: work that
    /// hangs on a silent VPN no longer holds the workspace, and ends without touching the entries of the new session.
    /// </summary>
    private readonly Dictionary<Guid, List<Work>> _running = [];

    private IEnumerable<Work> RunningOn(Guid workspaceId) => _running.TryGetValue(workspaceId, out var work) ? work : [];

    private Work Start(Guid workspaceId, bool statement)
    {
        var work = new Work(_time.GetUtcNow(), statement);
        (_running.TryGetValue(workspaceId, out var list) ? list : _running[workspaceId] = []).Add(work);
        shell.NotifyChanged();
        return work;
    }

    private void End(Guid workspaceId, Work work)
    {
        if (_running.TryGetValue(workspaceId, out var list) && list.Remove(work) && list.Count == 0)
        {
            _running.Remove(workspaceId);
        }

        shell.NotifyChanged();
    }

    /// <summary>
    /// A write, commit or rollback of this workspace is running. Per workspace (WP-24): a slow commit on one connection must
    /// not silently swallow Ctrl+S in another.
    /// </summary>
    public bool IsBusy(Guid workspaceId) => RunningOn(workspaceId).Any(w => !w.Statement);

    /// <summary>
    /// A write, commit, rollback or writing statement holds the workspace's session: the leave dialog waits for it or offers
    /// to give up the session (review 3.44.0, A1 – a statement whose tab was closed counted nowhere).
    /// </summary>
    public bool HoldsSession(Guid workspaceId) => RunningOn(workspaceId).Any();

    /// <summary>How long a write, commit, rollback or statement may run before giving up the session is offered.</summary>
    public static readonly TimeSpan StuckAfter = TimeSpan.FromSeconds(5);

    /// <summary>When the oldest work running on the workspace's session started; null if none runs.</summary>
    public DateTimeOffset? BusySince(Guid workspaceId) => RunningOn(workspaceId).Select(w => (DateTimeOffset?)w.Started).Min();

    /// <summary>
    /// Work on the session has run for <see cref="StuckAfter"/> (review 3.40.0, A2; statements since 3.44.0, A1): over a silent
    /// VPN it may never return – no command timeout ends it –, so the status bar and the leave dialog offer to give up the session.
    /// </summary>
    public bool IsStuck(Guid workspaceId) => StuckIn(workspaceId) <= TimeSpan.Zero;

    /// <summary>How long until the running work counts as stuck (zero or less: it is); null if nothing runs.</summary>
    public TimeSpan? StuckIn(Guid workspaceId) => BusySince(workspaceId) is { } since ? since + StuckAfter - _time.GetUtcNow() : null;

    /// <summary>
    /// A writing statement of this workspace is running (review 3.40.0, A1): it holds the editor and is not in its log yet –
    /// a commit would wait for it and commit it unseen, a rollback take it back unseen. Both are refused meanwhile.
    /// </summary>
    public bool IsStatementRunning(Guid workspaceId) => RunningOn(workspaceId).Any(w => w.Statement);

    /// <summary>Marks a writing statement of the workspace as running until disposed (<see cref="StatementRunner.WriteAsync"/>).</summary>
    public IDisposable StatementRunning(Guid workspaceId)
    {
        var work = Start(workspaceId, statement: true);
        return new Running(() => End(workspaceId, work));
    }

    private sealed class Running(Action ended) : IDisposable
    {
        private Action? _ended = ended;

        public void Dispose() => Interlocked.Exchange(ref _ended, null)?.Invoke();
    }

    /// <summary>
    /// An export of all rows reads through the workspace's session (review 3.44.0, A2): a write would wait for it without a
    /// word, and after a few seconds look like a hanging connection – giving up the session would cancel the export and roll
    /// the transaction back. Says so instead; null if no export reads there.
    /// </summary>
    public string? RefusedByExport(Guid workspaceId) =>
        shell.BackgroundReads.ReadingIn(workspaceId) is { } export ? TextFormat.Format(GridText.Editing_ExportReading, export.Label) : null;

    private void RefuseWhileStatementRuns(WorkspaceTabs workspace)
    {
        if (IsStatementRunning(workspace.WorkspaceId))
        {
            throw new RefusedException(GridText.Editing_StatementRunning);
        }
    }

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

    /// <summary>Workspaces with uncommitted work, of all open connections (quitting asks for all).</summary>
    public IReadOnlyList<WorkspaceTabs> WithWork() => shell.AllWorkspaces.Where(w => SummaryOf(w).HasWork).ToList();

    /// <summary>Writes the pending changes of all tabs; false if a problem stopped it (see <see cref="Problem"/>).</summary>
    public Task<bool> FlushAsync(WorkspaceTabs workspace) =>
        StoppedByForm(workspace) ? Task.FromResult(false) : RunAsync(workspace, () => FlushCoreAsync(workspace, duringCommit: false));

    /// <summary>Writes what is pending, then commits; false if a write problem stopped it.</summary>
    /// <param name="shown">
    /// The writes a confirmation listed (Prod, WP-30): refused if another one was written meanwhile – a statement of another
    /// tab finishing after the dialog opened would be committed unseen (R4a).
    /// </param>
    public Task<bool> CommitAsync(WorkspaceTabs workspace, IReadOnlyList<WriteAction>? shown = null) =>
        StoppedByForm(workspace) ? Task.FromResult(false) : RunAsync(workspace, async () =>
    {
        RefuseWhileStatementRuns(workspace);
        var editor = await WorkspacesOf(workspace.WorkspaceId).GetEditorAsync(workspace.WorkspaceId, CancellationToken.None);
        if (shown is not null && editor.Actions is [.., var newest] && (shown.Count == 0 || shown[^1].Id != newest.Id))
        {
            throw new RefusedException(TextFormat.Format(GridText.Editing_WrittenSinceConfirm, newest.Display));
        }

        // Grid writes of a transaction that ended without commit (a rollback that failed with the connection): they are not
        // in the database – committing would say so otherwise (R4a). Also writes of tabs closed since (review 3.31.0, A28).
        if (editor.Transaction.Mode != TransactionMode.ReadWrite
            && (Trackers(workspace).Any(t => t.FlushedCount > 0) || History.HasWritten(workspace.WorkspaceId)))
        {
            RestorePending(workspace);
            throw new RefusedException(GridText.Editing_TransactionGone);
        }

        if (!await FlushCoreAsync(workspace, duringCommit: true))
        {
            return false;
        }

        if (editor.Transaction.Mode == TransactionMode.ReadWrite)
        {
            try
            {
                await editor.CommitAsync(CancellationToken.None);
            }
            catch (DatabaseException ex) when (editor.Transaction.Mode != TransactionMode.ReadWrite)
            {
                // Oracle rolled the transaction back (ORA-02091, deferred constraint) or the session is gone: what was
                // written becomes pending again instead of vanishing from the grid.
                RestorePending(workspace);
                if (ex.IsConnectionLost)
                {
                    // The connection broke while the COMMIT was on its way (review 3.40.0, A6): whether it arrived is not
                    // known. Writing the changes again unchecked could insert rows twice – so say so, not "rolled back".
                    throw new DatabaseException(GridText.Editing_CommitOutcomeUnknown, ex.ErrorCode, ex);
                }

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
        RefuseWhileStatementRuns(workspace);
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
    /// Where a write came from (change overview): the table tab of a grid write, the SQL or LINQ tab of a statement; null
    /// if that tab is closed or unknown.
    /// </summary>
    public WorkspaceTab? SourceOf(WorkspaceTabs workspace, WriteAction action)
    {
        WorkspaceTab? tab = action.Kind == WriteActionKind.Grid
            ? History.Find(workspace.WorkspaceId, action.Id)?.Tab
            : History.StatementTab(workspace.WorkspaceId, action);
        return tab is not null && workspace.Tabs.Contains(tab) ? tab : null;
    }

    /// <summary>The tab and rows of a grid write, written or taken back; null if unknown.</summary>
    public GridWrite? GridWriteOf(Guid workspaceId, Guid actionId) => History.Find(workspaceId, actionId);

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
            // An "overwrite" chosen before belonged to this attempt: a later write checks the row again (review 3.31.0, A13).
            Overwrites(problem.WorkspaceId).Clear();
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

        if (problem.RedoOf is { } redo)
        {
            // A redo writes only its own changes (review 3.31.0, A14); discarded, there is nothing left to redo.
            if (choice != ProblemChoice.Discard)
            {
                await RedoGridAsync(workspace, redo);
            }

            return;
        }

        _ = problem.DuringCommit ? await CommitAsync(workspace) : await FlushAsync(workspace);
    }

    /// <summary>
    /// Rolls back the workspace's transaction before DDL, which would commit it (WP-22). Statements are taken back with it,
    /// grid writes become pending again – as after a failed commit – and pending edits stay: DDL never commits them (R4a:
    /// "discard" used to clear every tab's edits, although no transaction held them).
    /// </summary>
    public Task<bool> RollbackTransactionAsync(WorkspaceTabs workspace) => RunAsync(workspace, async () =>
    {
        RefuseWhileStatementRuns(workspace);
        if (WorkspacesOf(workspace.WorkspaceId).TransactionOf(workspace.WorkspaceId) is { Mode: TransactionMode.ReadWrite })
        {
            var editor = await WorkspacesOf(workspace.WorkspaceId).GetEditorAsync(workspace.WorkspaceId, CancellationToken.None);
            await editor.RollbackAsync(CancellationToken.None);
        }

        RestorePending(workspace);
        Forget(workspace.WorkspaceId);
        shell.ReloadTableTabs(workspace.WorkspaceId);
        return true;
    });

    /// <summary>
    /// The workspace's session was given up (<see cref="WorkspaceLifecycle.ResetSessionAsync"/>) and its transaction with it:
    /// grid writes become pending again (as after a failed commit), every table tab reloads.
    /// </summary>
    public void SessionReset(WorkspaceTabs workspace)
    {
        // What hangs on the old session no longer holds the workspace (review 3.44.0, A1): commit and rollback work again.
        _running.Remove(workspace.WorkspaceId);
        RestorePending(workspace);
        Forget(workspace.WorkspaceId);
        shell.ReloadTableTabs(workspace.WorkspaceId);
    }

    /// <summary>The workspace's transaction ended, or the workspace or its connection closed: nothing of it is kept.</summary>
    public void Forget(Guid workspaceId)
    {
        History.Forget(workspaceId);
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
        try
        {
            foreach (var tab in tabs)
            {
                if (!await FlushTabAsync(workspace, editor, tab, tab.Changes!.PendingOperations(), options, duringCommit))
                {
                    return false; // the problem dialog decides; "retry" keeps an "overwrite" chosen before
                }
            }
        }
        catch
        {
            Overwrites(workspace.WorkspaceId).Clear(); // not a write problem (connection lost …): no decision carries over (A13)
            throw;
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
                History.Written(workspace.WorkspaceId, action.Id, tab, batch);
                KeepRedoable(workspace.WorkspaceId);
            }

            return true;
        }
        catch (FlushException ex)
        {
            IReadOnlyList<LockHolder>? holders = null;
            if (ex is LockConflictException && ScopeOf(workspace.WorkspaceId).Active.Schema is { } schema)
            {
                holders = await TryAsync(() => schema.GetLockHoldersAsync(tracker.Table.Table.Ref, CancellationToken.None));

                // The waiting session holds a lock on the table itself once it wrote there before (review 3.40.0, A20): it was
                // listed as "your workspace" – the one that waits. Only the others hold it up.
                var own = WorkspacesOf(workspace.WorkspaceId).SessionTagOf(workspace.WorkspaceId);
                holders = holders?.Where(h => own is null || h.ClientInfo != own).ToList();
            }

            Problem = new FlushProblem(workspace.WorkspaceId, tab, ex, duringCommit, holders) { RedoOf = options.RedoOf };
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
        foreach (var write in History.TakeAll(workspace.WorkspaceId))
        {
            write.Tab.Changes?.UndoFlush(write.Batch);
            shell.RequestTabCommand(write.Tab, TabCommand.Reload);
        }
    }

    private void Finish(WorkspaceTabs workspace)
    {
        foreach (var tab in workspace.TableTabs.Where(t => t.Changes is not null))
        {
            tab.Changes!.Clear();
            if (!tab.DropStaleStructure()) // DDL changed the table while the changes held the old structure
            {
                shell.RequestTabCommand(tab, TabCommand.Reload);
            }
        }

        Forget(workspace.WorkspaceId);
    }

    private async Task<bool> RunAsync(WorkspaceTabs workspace, Func<Task<bool>> action)
    {
        if (IsBusy(workspace.WorkspaceId))
        {
            // Say so (review 3.31.0, A27): "Commit" in the leave dialog closed it without a word while a write hung on the VPN.
            shell.Notify(GridText.Editing_Busy);
            return false;
        }

        if (RefusedByExport(workspace.WorkspaceId) is { } exporting)
        {
            shell.Notify(exporting);
            return false;
        }

        var work = Start(workspace.WorkspaceId, statement: false);
        try
        {
            var result = await shell.CallDbAsync(logger, ScopeOf(workspace.WorkspaceId).Active, action);
            return shell.ShowFailure(result) && result.Value;
        }
        finally
        {
            End(workspace.WorkspaceId, work);
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

    private HashSet<Guid> Overwrites(Guid workspaceId) =>
        _overwrite.TryGetValue(workspaceId, out var set) ? set : _overwrite[workspaceId] = [];
}
