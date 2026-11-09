using FerretSharp.Core.Connections;
using FerretSharp.Core.Data;
using FerretSharp.Core.Query;
using FerretSharp.Core.Resources;
using FerretSharp.Core.Schema;
using FerretSharp.Core.Workspaces;
using FerretSharp.UI.Resources;

namespace FerretSharp.UI.State;

// The data diff of a workspace (WP-44): its start point, the tables it compares and the comparison, on the connection's read
// session – the committed state, never the workspace's own uncommitted work.
public sealed partial class WorkspaceLifecycle
{
    /// <summary>The start point of the workspace's data diff and its tables; null if none is set.</summary>
    public DataDiffState? DataDiffOf(Guid workspaceId) => ScopeOf(workspaceId).Workspaces.Find(workspaceId)?.DataDiff;

    /// <summary>Why a tab cannot be compared with an earlier state; null if it can (a table or materialized view).</summary>
    public static string? DataDiffUnavailable(TableTab tab) =>
        tab.Table.Kind == TableKind.View ? ShellText.DataDiff_NoViews
        // A SQL condition (WP-79) may read other tables, which a start point or time travel would read as now (decision of the user).
        : tab.ConditionApplied ? GridText.Condition_NotWithCondition
        : null;

    /// <summary>
    /// "Vergleich starten": the database's current SCN becomes the start point – or, with <paramref name="at"/>, the SCN of that
    /// time before now ("ab Zeitpunkt …", WP-78). The tables of an earlier start point stay; with none yet, the workspace's
    /// active table tab is the first, with its filter.
    /// </summary>
    public async Task StartDataDiffAsync(Guid workspaceId, DateTimeOffset? at = null)
    {
        var scope = ScopeOf(workspaceId);

        // A comparison running on the read session first: the SCN is read on the same session and would wait behind it, the
        // start point then lay long after the click (second review of WP-44).
        DiffTabOf(workspaceId)?.Cancel();
        shell.Notify(ShellText.DataDiff_Starting); // the first read opens the read session: over the VPN that takes a moment
        if (await PointAsync(scope.Active, at) is not { } point)
        {
            return;
        }

        var before = DataDiffOf(workspaceId);
        var state = new DataDiffState(point.Scn, at ?? DateTimeOffset.Now, before?.Entries ?? []) { Database = point.Database };
        if (state.Entries.Count == 0 && shell.FindWorkspace(workspaceId)?.ActiveTab is TableTab active && DataDiffUnavailable(active) is null)
        {
            state = state.WithEntry(active.Table.Ref, active.AppliedFilters);
        }

        scope.Workspaces.SetDataDiff(workspaceId, state);
        if (DiffTabOf(workspaceId) is { } tab)
        {
            tab.Cancel(); // a comparison of the old start point must not fill the new one (review of WP-44)
            tab.Clear();
        }

        shell.Notify(state.Entries.Count == 0
            ? TextFormat.Format(ShellText.DataDiff_StartedEmpty, state.StartedAt.LocalDateTime)
            : TextFormat.Format(ShellText.DataDiff_Started, state.StartedAt.LocalDateTime, string.Join(", ", state.Entries.Select(e => e.Table.Name))));
    }

    /// <summary>"Startpunkt verwerfen": the start point, its tables and what was found go.</summary>
    public void DiscardDataDiff(Guid workspaceId)
    {
        if (DiffTabOf(workspaceId) is { } tab)
        {
            tab.Cancel();
            tab.Clear();
        }

        ScopeOf(workspaceId).Workspaces.SetDataDiff(workspaceId, null);
        shell.NotifyChanged();
    }

    /// <summary>"Vergleichen": shows the workspace's diff tab and compares every table of the start point.</summary>
    public Task CompareDataDiffAsync(Guid workspaceId)
    {
        if (DataDiffOf(workspaceId) is not { } state || OpenDiffTab(workspaceId) is not { } tab)
        {
            return Task.CompletedTask;
        }

        return CompareAsync(workspaceId, tab, state, state.Entries);
    }

    /// <summary>"Diesen Tab vergleichen": the tab's table with its filter joins the start point (or takes the new filter) and is compared.</summary>
    public Task CompareTabAsync(TableTab table)
    {
        if (DataDiffUnavailable(table) is { } why)
        {
            shell.Notify(why);
            return Task.CompletedTask;
        }

        return AddToDataDiffAsync(table.WorkspaceId, table.Table.Ref, table.AppliedFilters);
    }

    /// <summary>A table (whole, or with a filter) joins the start point and is compared at once.</summary>
    public Task AddToDataDiffAsync(Guid workspaceId, TableRef table, IReadOnlyList<FilterCondition> filters)
    {
        if (DataDiffOf(workspaceId) is not { } state)
        {
            shell.Notify(ShellText.DataDiff_NoStartPoint);
            return Task.CompletedTask;
        }

        state = state.WithEntry(table, filters);
        ScopeOf(workspaceId).Workspaces.SetDataDiff(workspaceId, state);
        if (OpenDiffTab(workspaceId) is not { } tab)
        {
            return Task.CompletedTask;
        }

        tab.Selected = table;
        return CompareAsync(workspaceId, tab, state, [state.Entries.First(e => e.Table == table)]);
    }

    /// <summary>A table leaves the start point; what was found for it goes.</summary>
    public void RemoveFromDataDiff(Guid workspaceId, TableRef table)
    {
        if (DataDiffOf(workspaceId) is not { } state)
        {
            return;
        }

        ScopeOf(workspaceId).Workspaces.SetDataDiff(workspaceId, state.WithoutEntry(table));
        if (DiffTabOf(workspaceId) is { } tab)
        {
            tab.Forget(table);
            if (tab.Selected == table)
            {
                tab.Selected = null;
            }
        }

        shell.NotifyChanged();
    }

    private DataDiffTab? DiffTabOf(Guid workspaceId) => shell.FindWorkspace(workspaceId)?.Tabs.OfType<DataDiffTab>().FirstOrDefault();

    /// <summary>The workspace's diff tab, opened if needed; the workspace becomes the active one.</summary>
    private DataDiffTab? OpenDiffTab(Guid workspaceId)
    {
        ScopeOf(workspaceId).Workspaces.Activate(workspaceId);
        return shell.FindWorkspace(workspaceId) is { } workspace ? shell.OpenDataDiff(workspace) : null;
    }

    /// <summary>Compares <paramref name="entries"/> one after the other, each on the read session; a running comparison is not started twice.</summary>
    private async Task CompareAsync(Guid workspaceId, DataDiffTab tab, DataDiffState state, IReadOnlyList<DataDiffEntry> entries)
    {
        if (tab.Begin(state.Scn) is not { } token)
        {
            shell.Notify(ShellText.DataDiff_AlreadyRunning);
            return;
        }

        var scope = ScopeOf(workspaceId);
        try
        {
            // The start point must still belong to the database the connection reaches (WP-78): a profile switched to another
            // database, or one set up anew, would compare against nonsense.
            if (!await StillBelongsAsync(workspaceId, scope.Active, state, token))
            {
                return;
            }

            foreach (var entry in entries)
            {
                tab.Set(DataDiffResult.Pending(entry), state.Scn);
                tab.Selected ??= entry.Table;
                shell.NotifyChanged();
                tab.Set(await CompareOneAsync(scope.Active, state, entry, token), state.Scn);
                if (token.IsCancellationRequested)
                {
                    break;
                }
            }
        }
        finally
        {
            foreach (var entry in entries.Where(e => tab.ResultOf(e.Table) is { Reading: true }))
            {
                tab.Set(new DataDiffResult(entry, null, ShellText.DataDiff_Cancelled), state.Scn);
            }

            tab.End(DateTimeOffset.Now);
            shell.NotifyChanged();
        }
    }

    private async Task<DataDiffResult> CompareOneAsync(ActiveConnection active, DataDiffState state, DataDiffEntry entry, CancellationToken token)
    {
        if (active.Schema is not { } schema)
        {
            return new DataDiffResult(entry, null, ShellText.DataDiff_NotConnected);
        }

        if ((schema.Find(entry.Table) ?? ShellState.OpenedByName(entry.Table, schema)) is not { } summary)
        {
            return new DataDiffResult(entry, null, TextFormat.Format(ShellText.DataDiff_TableGone, entry.Table.Owner, entry.Table.Name));
        }

        if (summary.Kind == TableKind.View)
        {
            // A view from a workspace file edited by hand, or a table replaced by a view (review of WP-44): the query refuses it.
            return new DataDiffResult(entry, null, ShellText.DataDiff_NoViews);
        }

        var read = await shell.RunDbAsync(logger, active, async () =>
        {
            var details = await schema.GetDetailsAsync(summary, token);
            var rows = await active.Reads.ReadAsync((data, ct) => data.ReadDiffAsync(details, entry.Filters, state.Scn, ct), token);
            return DataDiff.Pair(details, entry.Filters, rows);
        }, token);
        if (read.Cancelled)
        {
            return new DataDiffResult(entry, null, ShellText.DataDiff_Cancelled);
        }

        return read.Error is { } error
            ? new DataDiffResult(entry, null, DataDiffErrors.Explain(error, entry.Table, state.StartedAt) ?? error.Display, read.IsRefusal ? null : error)
            : new DataDiffResult(entry, read.Value, null);
    }
}
