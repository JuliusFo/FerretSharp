using FerretSharp.Core.Connections;
using FerretSharp.Core.Data;
using FerretSharp.Core.Query;
using FerretSharp.Core.Schema;
using FerretSharp.Testing;
using FerretSharp.UI.Resources;
using FerretSharp.UI.State;

namespace FerretSharp.UI.Tests.State;

/// <summary>The data diff of a workspace (WP-44): start point, the tables it compares, the comparison on the read session.</summary>
public sealed class DataDiffTests : IAsyncDisposable
{
    private readonly TestApp _app = new();

    private WorkspaceLifecycle Lifecycle => _app.Lifecycle;

    private static readonly FilterCondition Open = new("ID", FilterOperator.Gt, ["10"]);

    private static DiffSideRow Row(bool now, decimal id) => new(now, new RowData(new RowKey.PrimaryKey([id]), [id]));

    /// <summary>A KUNDEN tab with a filter, active in the connection's workspace.</summary>
    private TableTab ActiveKunden(ConnectionScope scope)
    {
        var workspace = _app.WorkspaceOf(scope);
        var tab = new TableTab(workspace.WorkspaceId, TestApp.Kunden) { AppliedFilters = [Open] };
        workspace.Tabs.Add(tab);
        workspace.ActiveTab = tab;
        return tab;
    }

    [Fact]
    public async Task Starting_takes_the_active_table_tab_and_comparing_fills_the_diff_tab_from_the_read_session()
    {
        var scope = await _app.OpenAsync(TestApp.Profile("ERP Test"));
        var id = scope.Workspaces.Active!.Id;
        ActiveKunden(scope);
        var read = new List<(IReadOnlyList<FilterCondition> Filters, long Scn)>();
        _app.ReadDiff = (_, filters, scn) =>
        {
            read.Add((filters, scn));
            return Task.FromResult(new DiffRead([Row(true, 11), Row(false, 12)], false, new HashSet<int>(), RowKeyKind.PrimaryKey, false));
        };

        await Lifecycle.StartDataDiffAsync(id);

        var state = Lifecycle.DataDiffOf(id)!;
        Assert.Equal(4711, state.Scn);
        Assert.Equal([TestApp.Kunden.Ref], state.Entries.Select(e => e.Table));
        Assert.Equal([Open], state.Entries[0].Filters);

        await Lifecycle.CompareDataDiffAsync(id);

        var tab = Assert.IsType<DataDiffTab>(_app.WorkspaceOf(scope).ActiveTab);
        var diff = tab.ResultOf(TestApp.Kunden.Ref)!.Diff!;
        Assert.Equal([DiffStatus.Added, DiffStatus.Deleted], diff.Rows.Select(r => r.Status));
        var (filters, scn) = Assert.Single(read);
        Assert.Equal([Open], filters);
        Assert.Equal(4711, scn);
        Assert.False(tab.Running);
        Assert.True(scope.Active.Reads.HasSession); // the committed state, never the workspace's session
    }

    [Fact]
    public async Task A_missing_flashback_right_is_explained_and_a_new_start_point_keeps_the_tables_but_not_the_results()
    {
        var scope = await _app.OpenAsync(TestApp.Profile("ERP Test"));
        var id = scope.Workspaces.Active!.Id;
        ActiveKunden(scope);
        await Lifecycle.StartDataDiffAsync(id);
        _app.ReadDiff = (_, _, _) => Task.FromException<DiffRead>(new DatabaseException("insufficient privileges", "ORA-01031"));

        await Lifecycle.CompareDataDiffAsync(id);

        var tab = Assert.IsType<DataDiffTab>(_app.WorkspaceOf(scope).ActiveTab);
        var result = tab.ResultOf(TestApp.Kunden.Ref)!;
        Assert.Contains("GRANT FLASHBACK", result.Error, StringComparison.Ordinal);
        Assert.NotNull(result.Failure);

        _app.CurrentScn = 4800;
        await Lifecycle.StartDataDiffAsync(id);

        Assert.Equal(4800, Lifecycle.DataDiffOf(id)!.Scn);
        Assert.Single(Lifecycle.DataDiffOf(id)!.Entries);
        Assert.Null(tab.ResultOf(TestApp.Kunden.Ref));

        Lifecycle.DiscardDataDiff(id);
        Assert.Null(Lifecycle.DataDiffOf(id));
    }

    [Fact]
    public async Task A_view_cannot_be_compared_and_without_a_start_point_a_tab_is_not_added()
    {
        var scope = await _app.OpenAsync(TestApp.Profile("ERP Test"));
        var workspace = _app.WorkspaceOf(scope);
        var view = new TableTab(workspace.WorkspaceId, new TableSummary("APP_USER", "V_KUNDEN", TableKind.View));
        var table = ActiveKunden(scope);

        await Lifecycle.CompareTabAsync(view);
        Assert.Equal(ShellText.DataDiff_NoViews, _app.Shell.Notice?.Text);

        await Lifecycle.CompareTabAsync(table);
        Assert.Equal(ShellText.DataDiff_NoStartPoint, _app.Shell.Notice?.Text);
        Assert.Null(Lifecycle.DataDiffOf(workspace.WorkspaceId));
        Assert.DoesNotContain(workspace.Tabs, t => t is DataDiffTab);
    }

    public ValueTask DisposeAsync() => _app.DisposeAsync();
}
