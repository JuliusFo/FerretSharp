using FerretSharp.Core.Connections;
using FerretSharp.Core.Data;
using FerretSharp.Core.Resources;
using FerretSharp.Core.Schema;
using FerretSharp.Core.Workspaces;
using FerretSharp.UI.Resources;

namespace FerretSharp.UI.State;

// Points in the past (WP-78, ADR 0048): a time chosen for a time travel tab or a data diff turned into an SCN on the read
// session, the check that a saved point still belongs to the database, and setting rows of a diff back as pending changes.
public sealed partial class WorkspaceLifecycle
{
    /// <summary>
    /// The SCN of now (exactly where <c>V$DATABASE</c> may be read) or of <paramref name="at"/>, with the database it belongs to;
    /// null after a failure, shown already (a time too far back explained).
    /// </summary>
    private async Task<DatabasePoint?> PointAsync(ActiveConnection active, DateTimeOffset? at)
    {
        TimeSpan back = default;
        if (at is { } chosen)
        {
            (back, var error) = TimePoints.Back(chosen, DateTimeOffset.Now);
            if (error is not null)
            {
                shell.Notify(error);
                return null;
            }
        }

        var read = await shell.RunDbAsync(logger, active, () => at is null
            ? active.Reads.CurrentPointAsync(CancellationToken.None)
            : active.Reads.ReadAsync((data, ct) => data.PointBackAsync(back, ct), CancellationToken.None));
        if (read.Error is { } failure && !read.IsRefusal && at is { } time && DataDiffErrors.ExplainPoint(failure, time) is { } why)
        {
            shell.Notify(why);
            return null;
        }

        return shell.ShowFailure(read) ? read.Value : null;
    }

    /// <summary>
    /// How far back the undo reaches about (<c>V$UNDOSTAT</c>, WP-78) for the time dialog's hint; null without the right, a value
    /// or a connection – the hint is then left out, nothing is reported.
    /// </summary>
    public async Task<TimeSpan?> UndoReachAsync(Guid workspaceId, CancellationToken cancellationToken)
    {
        var active = ScopeOf(workspaceId).Active;
        var read = await shell.RunDbAsync(logger, active, () => active.Reads.ReadAsync((data, ct) => data.UndoReachAsync(ct), cancellationToken), cancellationToken);
        return read.Value;
    }

    /// <summary>
    /// The start point of the workspace's diff still belongs to the database the connection reaches; if not, it is dropped with
    /// a notice and false comes back. A failed read counts as belonging: the comparison names its own error then.
    /// </summary>
    private async Task<bool> StillBelongsAsync(Guid workspaceId, ActiveConnection active, DataDiffState state, CancellationToken token)
    {
        var now = await shell.RunDbAsync(logger, active, () => active.Reads.CurrentPointAsync(token), token);
        if (now.Value is not { } point || TimePoints.Mismatch(state.Scn, state.Database, point) is not { } why)
        {
            return !now.Cancelled;
        }

        DiscardDataDiff(workspaceId);
        shell.Notify(why);
        return false;
    }

    /// <summary>
    /// "Zeitreise …" of a table tab: the table as it was at <paramref name="at"/> in a new tab after it, read only, with the
    /// tab's filters and sort order.
    /// </summary>
    public async Task TimeTravelAsync(TableTab source, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (await PointAsync(ScopeOf(source.WorkspaceId).Active, at) is not { } point)
        {
            return;
        }

        shell.OpenTimeTravel(source.WorkspaceId, source.Table, source.AppliedFilters, source.Sorts, new TimeTravelState(point.Scn, at, point.Database));
    }

    /// <summary>"Stand zum Startpunkt öffnen" of the diff tab: the table of a diff entry as it was at the start point, with its filter.</summary>
    public void OpenAtStart(Guid workspaceId, DataDiffEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (DataDiffOf(workspaceId) is not { } state || ScopeOf(workspaceId).Active.Schema is not { } schema
            || (schema.Find(entry.Table) ?? ShellState.OpenedByName(entry.Table, schema)) is not { } table)
        {
            return;
        }

        shell.OpenTimeTravel(workspaceId, table, entry.Filters, [], new TimeTravelState(state.Scn, state.StartedAt, state.Database ?? ""));
    }

    /// <summary>
    /// A time travel tab restored from the workspace file: its point must still belong to the database (WP-78); if not, the tab
    /// closes with a notice. Checked once per tab; a failed read leaves it open and unchecked – its rows name their own error.
    /// </summary>
    public async Task CheckTimeTravelAsync(TableTab tab)
    {
        ArgumentNullException.ThrowIfNull(tab);
        if (tab.TimeTravel is not { } at || tab.PointChecked)
        {
            return;
        }

        var active = ScopeOf(tab.WorkspaceId).Active;
        var now = await shell.RunDbAsync(logger, active, () => active.Reads.CurrentPointAsync(CancellationToken.None));
        if (now.Value is not { } point)
        {
            return; // checked again when the tab is shown next (review of WP-78: a failed check counted as passed)
        }

        tab.PointChecked = true;
        if (TimePoints.Mismatch(at.Scn, at.Database, point) is { } why)
        {
            shell.CloseTab(tab);
            shell.Notify(why);
        }
    }

    /// <summary>
    /// "Auf Stand von … zurücksetzen" of the diff tab (WP-78): the chosen rows of a table become pending changes of its tab in
    /// the workspace – a deleted row an INSERT, a changed one an UPDATE to its values then, an added one a DELETE. Nothing is
    /// written; the tab (opened if needed) shows them, written the usual way. Read whole before: the values then on the read
    /// session, the values now on the workspace's.
    /// </summary>
    public async Task RestoreAsync(Guid workspaceId, TableDiff diff, IReadOnlyList<DiffRow> rows)
    {
        ArgumentNullException.ThrowIfNull(diff);
        ArgumentNullException.ThrowIfNull(rows);
        var scope = ScopeOf(workspaceId);
        if (RestoreUnavailable(workspaceId, diff, rows) is { } why)
        {
            shell.Notify(why);
            return;
        }

        if (DataDiffOf(workspaceId) is not { } state || scope.Active.Schema is not { } schema || rows.Count == 0
            || DiffTabOf(workspaceId) is not { } diffTab || diffTab.BeginRestore() is not { } token)
        {
            return;
        }

        var table = diff.Table;
        DbResult<RestoreRead> read;
        shell.NotifyChanged(); // the diff tab offers "Abbrechen" while the rows are read
        try
        {
            read = await shell.RunDbAsync(logger, scope.Active, async () =>
            {
                var now = await scope.Workspaces.GetDataAsync(workspaceId, token);
                return await DataRestore.ReadAsync(scope.Active.Reads.AsOf(state.Scn), now, table, rows, token);
            }, token);
        }
        finally
        {
            diffTab.EndRestore();
            shell.NotifyChanged();
        }

        if (read.Cancelled)
        {
            return;
        }

        if (read.Error is { } error && !read.IsRefusal && DataDiffErrors.Explain(error, table.Table.Ref, state.StartedAt, timeTravel: true) is { } explained)
        {
            shell.Notify(explained);
            return;
        }

        if (!shell.ShowFailure(read) || read.Value is not { } found)
        {
            return;
        }

        // The rows took a while: the workspace may have been locked, or started writing or committing meanwhile (review of WP-78).
        if (RestoreUnavailable(workspaceId, diff, rows) is { } nowWhy)
        {
            shell.Notify(nowWhy);
            return;
        }

        if ((schema.Find(table.Table.Ref) ?? ShellState.OpenedByName(table.Table.Ref, schema)) is not { } summary
            || OpenRestoreTab(workspaceId, summary) is not { } tab)
        {
            return;
        }

        if (tab.Changes is { } existing && !SameColumns(existing.Table, table))
        {
            shell.Notify(ShellText.Restore_StructureChanged);
            return;
        }

        var tracker = tab.Changes ??= new ChangeTracker(table);
        RestoreResult result;
        try
        {
            result = tracker.Restore(found.Rows);
        }
        catch (RefusedException ex)
        {
            shell.Notify(ex.Message);
            return;
        }

        shell.RequestTabCommand(tab, TabCommand.Reload); // the grid lays the pending changes over its rows, new ones on top
        shell.NotifyChanged();
        var skipped = new List<string>();
        if (result.AlreadyChanged > 0)
        {
            skipped.Add(TextFormat.Plural(result.AlreadyChanged, ShellText.Restore_AlreadyChangedOne, ShellText.Restore_AlreadyChangedOther));
        }

        if (result.NotRestorable > 0)
        {
            skipped.Add(TextFormat.Plural(result.NotRestorable, ShellText.Restore_NotRestorableOne, ShellText.Restore_NotRestorableOther));
        }

        if (found.TooLarge > 0)
        {
            skipped.Add(TextFormat.Plural(found.TooLarge, ShellText.Restore_TooLargeOne, ShellText.Restore_TooLargeOther, LobContent.Size(LobLimits.MaxLoadLength)));
        }

        if (found.Gone > 0)
        {
            skipped.Add(TextFormat.Plural(found.Gone, ShellText.Restore_GoneOne, ShellText.Restore_GoneOther));
        }

        shell.Notify(TextFormat.Plural(result.Restored, ShellText.Restore_DoneOne, ShellText.Restore_DoneOther, table.Table.Name, state.StartedAt.LocalDateTime), skipped);
    }

    /// <summary>Why the rows of <paramref name="diff"/> cannot be set back in the workspace; null if they can (the diff tab greys the button out).</summary>
    public string? RestoreUnavailable(Guid workspaceId, TableDiff diff, IReadOnlyList<DiffRow> rows)
    {
        ArgumentNullException.ThrowIfNull(diff);
        ArgumentNullException.ThrowIfNull(rows);
        var deleted = rows.Any(r => r.Status == DiffStatus.Deleted);
        return !ScopeOf(workspaceId).Workspaces.IsWritable(workspaceId) ? DataText.WorkspaceReadOnlyUnlockToEdit
            // Like the grid while it writes (review of WP-78): a commit now would clear the changes or take them along unseen.
            : Editing.IsBusy(workspaceId) ? ShellText.Restore_Busy
            // A row moved by row movement looks deleted and added: bringing the "deleted" one back would double it.
            : deleted && diff.RowMovement ? ShellText.Restore_RowMovement
            : ChangeTracker.RestoreUnavailable(diff.Table, deleted);
    }

    /// <summary>
    /// The workspace's tab of the table as it is now (not a time travel tab), opened after the diff tab if there is none – in
    /// that workspace, also when another connection is shown by now (review of WP-78).
    /// </summary>
    private TableTab? OpenRestoreTab(Guid workspaceId, TableSummary table)
    {
        ScopeOf(workspaceId).Workspaces.Activate(workspaceId);
        return shell.OpenTableIn(workspaceId, table) is { } tab && tab.WorkspaceId == workspaceId ? tab : null;
    }

    /// <summary>A tracker made for another version of the table addresses columns by position: only the same columns in the same order do.</summary>
    private static bool SameColumns(TableDetails tracked, TableDetails read) =>
        tracked.Columns.Select(c => c.Name).SequenceEqual(read.Columns.Select(c => c.Name), StringComparer.Ordinal);
}
