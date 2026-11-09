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
        _app.ReadDiff = (_, filters, scn, _) =>
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
        _app.ReadDiff = (_, _, _, _) => Task.FromException<DiffRead>(new DatabaseException("insufficient privileges", "ORA-01031"));

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

    /// <summary>Holds the next read of a diff until <see cref="TaskCompletionSource{TResult}.SetResult"/>; <c>Arrived</c> once it is read.</summary>
    private (TaskCompletionSource<DiffRead> Release, TaskCompletionSource Arrived, List<string> Read) HoldReads()
    {
        var release = new TaskCompletionSource<DiffRead>(TaskCreationOptions.RunContinuationsAsynchronously);
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var read = new List<string>();
        _app.ReadDiff = (table, _, _, ct) =>
        {
            lock (read)
            {
                read.Add(table.Table.Name);
            }

            arrived.TrySetResult();
            return release.Task.WaitAsync(ct); // like the session: a cancel ends the query
        };
        return (release, arrived, read);
    }

    private static readonly DiffRead OneNew = new([Row(true, 11)], false, new HashSet<int>(), RowKeyKind.PrimaryKey, false);

    /// <summary>Review of WP-44: a comparison still running for the old start point filled the list of the new one.</summary>
    [Fact]
    public async Task A_new_start_point_drops_what_a_running_comparison_of_the_old_one_finds()
    {
        var scope = await _app.OpenAsync(TestApp.Profile("ERP Test"));
        var id = scope.Workspaces.Active!.Id;
        ActiveKunden(scope);
        await Lifecycle.StartDataDiffAsync(id);
        var (release, arrived, _) = HoldReads();

        var comparing = Lifecycle.CompareDataDiffAsync(id);
        await arrived.Task.WaitAsync(Wait.Limit, TestContext.Current.CancellationToken);
        var tab = _app.WorkspaceOf(scope).Tabs.OfType<DataDiffTab>().Single();
        _app.CurrentScn = 4800;
        var runningAtScn = true;
        _app.ScnRead = () => runningAtScn = !comparing.IsCompleted;
        await Lifecycle.StartDataDiffAsync(id);
        await comparing;

        Assert.False(runningAtScn); // second review: the SCN waited behind the running query on the same session
        Assert.Null(tab.ResultOf(TestApp.Kunden.Ref));
        Assert.False(tab.Running);
    }

    /// <summary>Review of WP-44: a closed diff tab went on comparing, and nothing could cancel it any more.</summary>
    [Fact]
    public async Task Closing_the_diff_tab_stops_its_comparison_after_the_running_table()
    {
        var scope = await _app.OpenAsync(TestApp.Profile("ERP Test"));
        var id = scope.Workspaces.Active!.Id;
        ActiveKunden(scope);
        _app.Tables = [TestApp.Kunden, new TableSummary("APP_USER", "AUFTRAG", TableKind.Table)];
        await scope.Active.Schema!.RefreshAsync(TestContext.Current.CancellationToken);
        await Lifecycle.StartDataDiffAsync(id);
        scope.Workspaces.SetDataDiff(id, Lifecycle.DataDiffOf(id)!.WithEntry(new TableRef("APP_USER", "AUFTRAG"), []));
        var (release, arrived, read) = HoldReads();

        var comparing = Lifecycle.CompareDataDiffAsync(id);
        await arrived.Task.WaitAsync(Wait.Limit, TestContext.Current.CancellationToken);
        _app.Shell.CloseTab(_app.WorkspaceOf(scope).Tabs.OfType<DataDiffTab>().Single());
        release.SetResult(OneNew);
        await comparing;

        Assert.Equal(["KUNDEN"], read);
    }

    /// <summary>Second review of WP-44: closing the workspace went on comparing as well.</summary>
    [Fact]
    public async Task Closing_the_workspace_stops_its_comparison()
    {
        var scope = await _app.OpenAsync(TestApp.Profile("ERP Test"));
        var id = scope.Workspaces.Active!.Id;
        ActiveKunden(scope);
        _app.Tables = [TestApp.Kunden, new TableSummary("APP_USER", "AUFTRAG", TableKind.Table)];
        await scope.Active.Schema!.RefreshAsync(TestContext.Current.CancellationToken);
        await Lifecycle.StartDataDiffAsync(id);
        scope.Workspaces.SetDataDiff(id, Lifecycle.DataDiffOf(id)!.WithEntry(new TableRef("APP_USER", "AUFTRAG"), []));
        await scope.Workspaces.CreateAsync("Other");
        var (release, arrived, read) = HoldReads();

        var comparing = Lifecycle.CompareDataDiffAsync(id);
        await arrived.Task.WaitAsync(Wait.Limit, TestContext.Current.CancellationToken);
        await scope.Workspaces.CloseAsync(id);
        _app.Shell.SyncWorkspaces(scope.Id, scope.Workspaces.Open, scope.Workspaces.Active?.Id, scope.Active.Schema!);
        release.SetResult(OneNew);
        await comparing.WaitAsync(Wait.Limit, TestContext.Current.CancellationToken);

        Assert.Equal(["KUNDEN"], read);
    }

    /// <summary>Review of WP-44: a view in the start point (a file edited by hand) threw out of the comparison.</summary>
    [Fact]
    public async Task A_view_in_the_start_point_is_reported_and_the_other_tables_are_compared()
    {
        var scope = await _app.OpenAsync(TestApp.Profile("ERP Test"));
        var id = scope.Workspaces.Active!.Id;
        var view = new TableSummary("APP_USER", "V_KUNDEN", TableKind.View);
        _app.Tables = [view, TestApp.Kunden];
        await scope.Active.Schema!.RefreshAsync(TestContext.Current.CancellationToken);
        ActiveKunden(scope);
        await Lifecycle.StartDataDiffAsync(id);
        var state = Lifecycle.DataDiffOf(id)!;
        scope.Workspaces.SetDataDiff(id, state with { Entries = [new(view.Ref, []), .. state.Entries] });

        await Lifecycle.CompareDataDiffAsync(id);

        var tab = _app.WorkspaceOf(scope).Tabs.OfType<DataDiffTab>().Single();
        Assert.Equal(ShellText.DataDiff_NoViews, tab.ResultOf(view.Ref)!.Error);
        Assert.NotNull(tab.ResultOf(TestApp.Kunden.Ref)!.Diff);
    }

    public ValueTask DisposeAsync() => _app.DisposeAsync();
}
