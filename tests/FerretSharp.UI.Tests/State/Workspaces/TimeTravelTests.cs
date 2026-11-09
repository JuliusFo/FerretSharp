using FerretSharp.Core.Connections;
using FerretSharp.Core.Data;
using FerretSharp.Core.Query;
using FerretSharp.Core.Resources;
using FerretSharp.Core.Schema;
using FerretSharp.Core.Workspaces;
using FerretSharp.Testing;
using FerretSharp.UI.State;

namespace FerretSharp.UI.Tests.State;

/// <summary>Points in the past (WP-78, ADR 0048): time travel tabs, a diff from a chosen time, setting rows back.</summary>
public sealed class TimeTravelTests : IAsyncDisposable
{
    private readonly TestApp _app = new();

    private WorkspaceLifecycle Lifecycle => _app.Lifecycle;

    private static readonly FilterCondition Open = new("ID", FilterOperator.Gt, ["10"]);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private TableTab ActiveKunden(ConnectionScope scope)
    {
        var workspace = _app.WorkspaceOf(scope);
        var tab = new TableTab(workspace.WorkspaceId, TestApp.Kunden) { AppliedFilters = [Open], Sorts = [new SortSpec("ID", true)] };
        workspace.Tabs.Add(tab);
        workspace.ActiveTab = tab;
        return tab;
    }

    [Fact]
    public async Task Time_travel_opens_a_tab_of_the_chosen_time_that_reads_as_of_its_scn_on_the_read_session()
    {
        var scope = await _app.OpenAsync(TestApp.Profile("ERP Test"));
        var source = ActiveKunden(scope);
        TimeSpan? back = null;
        _app.PointBack = asked =>
        {
            back = asked;
            return new DatabasePoint(4000, "FREE/FREEPDB1");
        };
        var at = DateTimeOffset.Now.AddMinutes(-15);

        await Lifecycle.TimeTravelAsync(source, at);

        var tab = Assert.IsType<TableTab>(_app.WorkspaceOf(scope).ActiveTab);
        Assert.NotSame(source, tab);
        Assert.Equal(new TimeTravelState(4000, at, "FREE/FREEPDB1"), tab.TimeTravel);
        Assert.Equal([Open], tab.AppliedFilters);
        Assert.Equal(source.Sorts, tab.Sorts);
        Assert.InRange(back!.Value, TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(15.5)); // the server's clock decides, not a zone
        Assert.Equal(new TimeTravelState(4000, at, "FREE/FREEPDB1"), tab.ToState(_app.WorkspaceOf(scope).Tabs).TimeTravel); // saved with the tab

        var data = await TabData.GetAsync(tab, scope.Workspaces, scope.Active, Ct);
        await data.CountAsync(_app.KundenDetails, [Open], Ct);

        Assert.Equal([4000L], _app.AsOfReads);
        Assert.True(scope.Active.Reads.HasSession); // never the workspace's session
    }

    [Fact]
    public async Task A_jump_from_a_time_travel_tab_stays_at_its_point_and_the_explorer_opens_the_table_as_it_is_now()
    {
        var scope = await _app.OpenAsync(TestApp.Profile("ERP Test"));
        var now = ActiveKunden(scope);
        var travel = _app.Shell.OpenTimeTravel(now.WorkspaceId, TestApp.Kunden, [], [], new TimeTravelState(4000, DateTimeOffset.Now, "FREE/FREEPDB1"))!;

        var jumped = _app.Shell.OpenFiltered(TestApp.Kunden, [Open])!;
        Assert.Equal(travel.TimeTravel, jumped.TimeTravel);
        Assert.Same(travel, jumped.Origin);

        Assert.Null(_app.Shell.OpenFiltered(TestApp.Kunden, [Open], now: true)!.TimeTravel); // "Aktuellen Stand öffnen"
        Assert.Same(now, _app.Shell.OpenTable(TestApp.Kunden)); // the explorer never lands in the past
    }

    [Fact]
    public async Task A_time_in_the_future_or_beyond_the_database_s_mapping_is_explained_and_opens_nothing()
    {
        var scope = await _app.OpenAsync(TestApp.Profile("ERP Test"));
        var source = ActiveKunden(scope);

        await Lifecycle.TimeTravelAsync(source, DateTimeOffset.Now.AddMinutes(5));
        Assert.Equal(DataText.TimeInFuture, _app.Shell.Notice?.Text);

        _app.PointBack = _ => throw new DatabaseException("time not in the mapping", "ORA-08180");
        await Lifecycle.TimeTravelAsync(source, DateTimeOffset.Now.AddDays(-30));

        Assert.Contains("ORA-08180", _app.Shell.Notice?.Text, StringComparison.Ordinal);
        Assert.Same(source, _app.WorkspaceOf(scope).ActiveTab);
    }

    [Fact]
    public async Task A_diff_can_start_at_a_chosen_time_and_a_start_point_of_another_database_is_dropped()
    {
        var scope = await _app.OpenAsync(TestApp.Profile("ERP Test"));
        var id = scope.Workspaces.Active!.Id;
        ActiveKunden(scope);
        _app.PointBack = _ => new DatabasePoint(3000, "FREE/FREEPDB1");
        var at = DateTimeOffset.Now.AddHours(-1);

        await Lifecycle.StartDataDiffAsync(id, at);

        var state = Lifecycle.DataDiffOf(id)!;
        Assert.Equal((3000L, at, "FREE/FREEPDB1"), (state.Scn, state.StartedAt, state.Database));

        _app.CurrentDatabase = "PROD/ERP"; // the profile was switched to another database
        var compared = 0;
        _app.ReadDiff = (_, _, _, _) =>
        {
            compared++;
            return Task.FromResult(new DiffRead([], false, new HashSet<int>(), RowKeyKind.PrimaryKey, false));
        };
        await Lifecycle.CompareDataDiffAsync(id);

        Assert.Null(Lifecycle.DataDiffOf(id));
        Assert.Equal(0, compared);
        Assert.Contains("PROD/ERP", _app.Shell.Notice?.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_time_travel_tab_from_the_file_of_another_database_closes()
    {
        var scope = await _app.OpenAsync(TestApp.Profile("ERP Test"));
        ActiveKunden(scope);
        var tab = _app.Shell.OpenTimeTravel(scope.Workspaces.Active!.Id, TestApp.Kunden, [], [], new TimeTravelState(9999, DateTimeOffset.Now, "FREE/FREEPDB1"))!;
        tab.PointChecked = false; // as restored from the file

        await Lifecycle.CheckTimeTravelAsync(tab); // SCN 9999 lies ahead of the database (4711): set up anew

        Assert.DoesNotContain(tab, _app.WorkspaceOf(scope).Tabs);
        Assert.Equal(DataText.PointAheadOfDatabase, _app.Shell.Notice?.Text);
    }

    private static readonly TableDetails Kunden = new(
        TestApp.Kunden,
        [new ColumnInfo("ID", "NUMBER", null, false, 10, 0, false, false, null, 1), new ColumnInfo("NAME", "VARCHAR2", 50, false, null, null, true, false, null, 2)],
        ["ID"], [], false);

    private static RowData Row(decimal id, string name) => new(new RowKey.PrimaryKey([id]), [id, name]);

    private static DiffRow Diff(DiffStatus status, decimal id) => new(status, new RowKey.PrimaryKey([id]), null, null, new HashSet<int>());

    [Fact]
    public async Task Setting_rows_back_makes_pending_changes_in_the_table_s_tab_from_the_values_then_and_now()
    {
        _app.KundenDetails = Kunden;
        var scope = await _app.OpenAsync(TestApp.Profile("ERP Test"));
        var id = scope.Workspaces.Active!.Id;
        var tab = ActiveKunden(scope);
        await Lifecycle.StartDataDiffAsync(id);
        _app.ByKeys = (scn, _, keys) => scn is null
            ? [Row(11, "added since"), Row(13, "Neu")]
            : [Row(12, "deleted since"), Row(13, "Alt")];
        var diff = new TableDiff(Kunden, [], [Diff(DiffStatus.Added, 11), Diff(DiffStatus.Deleted, 12), Diff(DiffStatus.Changed, 13)], false,
            new HashSet<int>(), RowKeyKind.PrimaryKey, false);

        await Lifecycle.RestoreAsync(id, diff, diff.Rows);

        Assert.Same(tab, _app.WorkspaceOf(scope).ActiveTab); // the tab of now, shown
        var operations = tab.Changes!.PendingOperations();
        Assert.Equal([OperationKind.Insert, OperationKind.Update, OperationKind.Delete], operations.Select(o => o.Kind).Order());
        Assert.Equal("deleted since", operations.Single(o => o.Kind == OperationKind.Insert).Values[1]);
        Assert.Equal("Alt", operations.Single(o => o.Kind == OperationKind.Update).Values[1]);
        Assert.Equal([4711L], _app.AsOfReads.Distinct()); // the values then as of the start point
        Assert.Equal(TextFormat.Plural(3, UI.Resources.ShellText.Restore_DoneOne, UI.Resources.ShellText.Restore_DoneOther, "KUNDEN",
            Lifecycle.DataDiffOf(id)!.StartedAt.LocalDateTime), _app.Shell.Notice?.Text);
    }

    [Fact]
    public async Task Setting_rows_back_is_refused_in_a_locked_workspace()
    {
        _app.KundenDetails = Kunden;
        var scope = await _app.OpenAsync(TestApp.Profile("ERP Prod", ConnectionKind.Prod));
        var id = scope.Workspaces.Active!.Id;
        var tab = ActiveKunden(scope);
        await Lifecycle.StartDataDiffAsync(id); // reading is allowed on Prod
        var diff = new TableDiff(Kunden, [], [Diff(DiffStatus.Deleted, 12)], false, new HashSet<int>(), RowKeyKind.PrimaryKey, false);

        Assert.Equal(DataText.WorkspaceReadOnlyUnlockToEdit, Lifecycle.RestoreUnavailable(id, diff, diff.Rows));
        await Lifecycle.RestoreAsync(id, diff, diff.Rows);

        Assert.Null(tab.Changes);
        Assert.Empty(_app.AsOfReads); // nothing was even read
    }

    [Fact]
    public void The_time_dialog_reads_local_times_to_the_second_and_offers_times_before_now()
    {
        var now = new DateTimeOffset(2026, 11, 9, 10, 30, 15, 500, TimeSpan.FromHours(1)).ToLocalTime();

        Assert.Equal(now.AddTicks(-now.Ticks % TimeSpan.TicksPerSecond).AddMinutes(-15), TimeChoice.Preset(15, now));
        var input = TimeChoice.ToInput(TimeChoice.Preset(15, now));
        Assert.Equal(TimeChoice.Preset(15, now), TimeChoice.FromInput(input));
        Assert.Equal(new DateTime(2026, 11, 9, 9, 30, 0, DateTimeKind.Local), TimeChoice.FromInput("2026-11-09T09:30")!.Value.LocalDateTime); // Chromium drops ":00"
        Assert.Null(TimeChoice.FromInput("09:30"));
        Assert.Equal(now.AddHours(-3), TimeChoice.Earliest(TimeSpan.FromHours(3), now));
        Assert.Null(TimeChoice.Earliest(null, now));
        Assert.Equal("2026-11-09T09:15:15", TimeChoice.ToInput(new DateTimeOffset(2026, 11, 9, 9, 15, 15, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 11, 9)))),
            StringComparer.Ordinal);
    }

    public ValueTask DisposeAsync() => _app.DisposeAsync();
}
