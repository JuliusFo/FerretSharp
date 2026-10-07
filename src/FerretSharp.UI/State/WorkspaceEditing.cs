using FerretSharp.Core.Connections;
using FerretSharp.Core.Data;
using FerretSharp.Core.Query;
using FerretSharp.Core.Settings;
using FerretSharp.Core.Workspaces;
using Microsoft.Extensions.Logging;

namespace FerretSharp.UI.State;

/// <summary>Counts for the status bar of one workspace.</summary>
/// <param name="Transaction">The workspace session's transaction; null while the session is not open.</param>
/// <param name="Actions">Uncommitted writes – grid flushes and SQL/LINQ statements –, oldest first.</param>
public sealed record EditSummary(int Pending, int Flushed, TransactionInfo? Transaction, IReadOnlyList<WriteAction> Actions)
{
    public bool HasWork => Pending > 0 || Flushed > 0 || Transaction?.Mode == TransactionMode.ReadWrite;

    public bool CanUndo => Actions.Count > 0;
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
/// and commits, rollback discards everything, undo takes back the last write – of the grid or a statement, since they
/// share the transaction (<see cref="IDataEditor.Actions"/>). Owned by the shell.
/// </summary>
public sealed class WorkspaceEditing(
    ShellState shell, ConnectionHub hub, AppSettingsService settings, ILogger<WorkspaceEditing> logger)
{
    /// <summary>The connection a workspace belongs to (WP-24: several can be open; a background one may still finish a write).</summary>
    private ConnectionScope ScopeOf(Guid workspaceId) => hub.OwnerOf(workspaceId) ?? hub.Shown;

    private WorkspaceManager WorkspacesOf(Guid workspaceId) => ScopeOf(workspaceId).Workspaces;

    /// <summary>Per workspace: the grid writes with what to restore when undone, in the order written.</summary>
    private readonly Dictionary<Guid, List<(Guid ActionId, TableTab Tab, FlushBatch Batch)>> _batches = [];
    private readonly Dictionary<Guid, HashSet<Guid>> _overwrite = [];

    /// <summary>A write, commit or rollback is running (buttons disabled).</summary>
    public bool Busy { get; private set; }

    public FlushProblem? Problem { get; private set; }

    public static IEnumerable<ChangeTracker> Trackers(WorkspaceTabs workspace) =>
        workspace.TableTabs.Select(t => t.Changes).OfType<ChangeTracker>();

    public EditSummary SummaryOf(WorkspaceTabs workspace) => new(
        Trackers(workspace).Sum(t => t.PendingCount),
        Trackers(workspace).Sum(t => t.FlushedCount),
        WorkspacesOf(workspace.WorkspaceId).TransactionOf(workspace.WorkspaceId),
        WorkspacesOf(workspace.WorkspaceId).ActionsOf(workspace.WorkspaceId));

    /// <summary>What became of a statement's write (<paramref name="action"/>, run in the transaction that began at <paramref name="transactionStart"/>).</summary>
    public static WriteFate FateOf(WorkspaceManager workspaces, Guid workspaceId, Guid action, DateTimeOffset? transactionStart)
    {
        if (workspaces.ActionsOf(workspaceId).Any(a => a.Id == action))
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
    public Task<bool> FlushAsync(WorkspaceTabs workspace) => RunAsync(workspace, () => FlushCoreAsync(workspace, duringCommit: false));

    /// <summary>Writes what is pending, then commits; false if a write problem stopped it.</summary>
    public Task<bool> CommitAsync(WorkspaceTabs workspace) => RunAsync(workspace, async () =>
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
        shell.Notify("Änderungen committed.");
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
        shell.Notify("Änderungen verworfen (Rollback).");
        return true;
    });

    /// <summary>
    /// Takes back the last write: rollback to its savepoint. A grid write's changes become pending again; a statement's
    /// rows are as before it (the tabs reload, its result says "zurückgenommen").
    /// </summary>
    public Task<bool> UndoLastAsync(WorkspaceTabs workspace) => RunAsync(workspace, async () =>
    {
        if (WorkspacesOf(workspace.WorkspaceId).ActionsOf(workspace.WorkspaceId).Count == 0)
        {
            return false;
        }

        var editor = await WorkspacesOf(workspace.WorkspaceId).GetEditorAsync(workspace.WorkspaceId, CancellationToken.None);
        if (await editor.UndoLastAsync(CancellationToken.None) is not { } action)
        {
            return false;
        }

        var batches = Batches(workspace.WorkspaceId);
        if (batches.FindLastIndex(b => b.ActionId == action.Id) is >= 0 and var index)
        {
            var (_, tab, batch) = batches[index];
            batches.RemoveAt(index);
            tab.Changes?.UndoFlush(batch);
            shell.RequestTabCommand(tab, TabCommand.Reload);
            shell.Notify($"Zurückgenommen: {action.Display} – die Änderungen sind wieder ausstehend.");
        }
        else
        {
            // A statement: any table tab of the workspace may show rows it had changed.
            foreach (var tab in workspace.TableTabs)
            {
                shell.RequestTabCommand(tab, TabCommand.Reload);
            }

            shell.Notify($"Zurückgenommen: {action.Display}.");
        }

        return true;
    });

    /// <summary>The statements a write would run, with values (for "Änderungen als SQL anzeigen").</summary>
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

        return parts.Count == 0 ? "-- Keine ausstehenden Änderungen." : string.Join(Environment.NewLine + Environment.NewLine, parts);
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
    public void Forget(Guid workspaceId)
    {
        _batches.Remove(workspaceId);
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
            var tracker = tab.Changes!;
            var operations = tracker.PendingOperations();
            try
            {
                var result = await editor.FlushAsync(tracker.Table, operations, options, CancellationToken.None);
                var batch = tracker.MarkFlushed(operations, result.NewKeys);
                if (result.Action is { } action)
                {
                    Batches(workspace.WorkspaceId).Add((action.Id, tab, batch));
                }
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

        Overwrites(workspace.WorkspaceId).Clear();
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
        if (Busy)
        {
            return false;
        }

        Busy = true;
        shell.NotifyChanged();
        try
        {
            var result = await shell.CallDbAsync(logger, ScopeOf(workspace.WorkspaceId).Profile, action);
            return shell.ShowFailure(result) && result.Value;
        }
        finally
        {
            Busy = false;
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

    private HashSet<Guid> Overwrites(Guid workspaceId) =>
        _overwrite.TryGetValue(workspaceId, out var set) ? set : _overwrite[workspaceId] = [];
}
