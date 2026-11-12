using FerretSharp.Core.Connections;
using FerretSharp.Core.Data;
using FerretSharp.Core.Query;
using FerretSharp.Core.Resources;
using FerretSharp.Core.Schema;
using FerretSharp.Core.Workspaces;
using FerretSharp.Testing;
using FerretSharp.UI.Resources;
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
        var at = _app.Time.GetLocalNow().AddMinutes(-15);

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
        var travel = _app.Shell.OpenTimeTravel(now.WorkspaceId, TestApp.Kunden, [], [], new TimeTravelState(4000, _app.Time.GetLocalNow(), "FREE/FREEPDB1"))!;

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

        await Lifecycle.TimeTravelAsync(source, _app.Time.GetLocalNow().AddMinutes(5));
        Assert.Equal(DataText.TimeInFuture, _app.Shell.Notice?.Text);

        _app.PointBack = _ => throw new DatabaseException("time not in the mapping", "ORA-08180");
        await Lifecycle.TimeTravelAsync(source, _app.Time.GetLocalNow().AddDays(-30));

        Assert.Contains("ORA-08180", _app.Shell.Notice?.Text, StringComparison.Ordinal);
        Assert.Same(source, _app.WorkspaceOf(scope).ActiveTab);
    }

    /// <summary>
    /// Review 3.70.0, A1: the lookup of the point hung on a silent read session with no way out. Past its limit it ends with a
    /// notice, and the read session is given up – the workspace's session and its transaction are not touched.
    /// </summary>
    [Fact]
    public async Task A_point_lookup_past_its_limit_gives_up_only_the_read_session()
    {
        var scope = await _app.OpenAsync(TestApp.Profile("ERP Test"));
        var source = ActiveKunden(scope);
        var workspaceSession = await scope.Workspaces.GetEditorAsync(source.WorkspaceId, Ct);
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource();
        _app.PointBack = _ =>
        {
            entered.TrySetResult();
            release.Wait(Wait.Limit); // the line is silent
            return new DatabasePoint(4000, "FREE/FREEPDB1");
        };

        var travel = Lifecycle.TimeTravelAsync(source, _app.Time.GetLocalNow().AddMinutes(-15));
        await entered.Task.WaitAsync(Wait.Limit, Ct);
        _app.Time.Advance(WorkspaceLifecycle.PointLimit);
        await travel.WaitAsync(Wait.Limit, Ct);
        release.Set();

        Assert.Equal(TextFormat.Format(ShellText.Lifecycle_PointTimedOut, 30), _app.Shell.Notice?.Text);
        Assert.False(scope.Active.Reads.HasSession);
        Assert.Same(source, _app.WorkspaceOf(scope).ActiveTab);
        Assert.Same(workspaceSession, await scope.Workspaces.GetEditorAsync(source.WorkspaceId, Ct)); // never the workspace's session
    }

    [Fact]
    public async Task A_diff_can_start_at_a_chosen_time_and_a_start_point_of_another_database_is_dropped()
    {
        var scope = await _app.OpenAsync(TestApp.Profile("ERP Test"));
        var id = scope.Workspaces.Active!.Id;
        ActiveKunden(scope);
        _app.PointBack = _ => new DatabasePoint(3000, "FREE/FREEPDB1");
        var at = _app.Time.GetLocalNow().AddHours(-1);

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
        var tab = _app.Shell.OpenTimeTravel(scope.Workspaces.Active!.Id, TestApp.Kunden, [], [], new TimeTravelState(9999, _app.Time.GetLocalNow(), "FREE/FREEPDB1"))!;
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
        _app.Shell.OpenDataDiff(_app.WorkspaceOf(scope)); // set back from the diff tab, which can cancel the read
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

    /// <summary>
    /// Review 3.70.0, C3: the tab's changes were tracked with the columns of before (a column added since): without the
    /// check the tracker laid the values by position into the wrong columns.
    /// </summary>
    [Fact]
    public async Task Rows_are_not_set_back_into_a_tab_whose_changes_have_other_columns()
    {
        _app.KundenDetails = Kunden;
        var scope = await _app.OpenAsync(TestApp.Profile("ERP Test"));
        var id = scope.Workspaces.Active!.Id;
        var tab = ActiveKunden(scope);
        tab.Changes = new ChangeTracker(Kunden);
        await Lifecycle.StartDataDiffAsync(id);
        _app.Shell.OpenDataDiff(_app.WorkspaceOf(scope));
        _app.ByKeys = (scn, _, _) => scn is null ? [] : [Row(12, "deleted since")];
        var wider = Kunden with { Columns = [.. Kunden.Columns, new ColumnInfo("FAX", "VARCHAR2", 30, true, null, null, false, false, null, 99)] };
        var diff = new TableDiff(wider, [], [Diff(DiffStatus.Deleted, 12)], false, new HashSet<int>(), RowKeyKind.PrimaryKey, false);

        await Lifecycle.RestoreAsync(id, diff, diff.Rows);

        Assert.Equal(UI.Resources.ShellText.Restore_StructureChanged, _app.Shell.Notice?.Text);
        Assert.Empty(tab.Changes.PendingOperations());
    }

    [Fact]
    public async Task Setting_rows_back_is_refused_in_a_locked_workspace_and_for_rows_moved_by_row_movement()
    {
        _app.KundenDetails = Kunden;
        var scope = await _app.OpenAsync(TestApp.Profile("ERP Prod", ConnectionKind.Prod));
        var id = scope.Workspaces.Active!.Id;
        var tab = ActiveKunden(scope);
        await Lifecycle.StartDataDiffAsync(id); // reading is allowed on Prod
        _app.Shell.OpenDataDiff(_app.WorkspaceOf(scope));
        var diff = new TableDiff(Kunden, [], [Diff(DiffStatus.Deleted, 12)], false, new HashSet<int>(), RowKeyKind.PrimaryKey, false);

        Assert.Equal(DataText.WorkspaceReadOnlyUnlockToEdit, Lifecycle.RestoreUnavailable(id, diff, diff.Rows));
        await Lifecycle.RestoreAsync(id, diff, diff.Rows);

        Assert.Null(tab.Changes);
        Assert.Empty(_app.AsOfReads); // nothing was even read

        // Review of WP-78: by ROWID with row movement a moved row looks deleted and new – bringing it back doubled it.
        await Lifecycle.UnlockAsync(id);
        var moved = diff with { RowKey = RowKeyKind.RowId, RowMovement = true };
        Assert.Equal(UI.Resources.ShellText.Restore_RowMovement, Lifecycle.RestoreUnavailable(id, moved, moved.Rows));
        Assert.Null(Lifecycle.RestoreUnavailable(id, moved, [Diff(DiffStatus.Changed, 13)]));
    }

    /// <summary>
    /// Review of WP-78: the changes went to the tab of the workspace shown when the rows had been read – another connection's,
    /// if the user switched meanwhile – where a write and commit would have run them against another database.
    /// </summary>
    [Fact]
    public async Task Rows_set_back_land_in_the_diff_s_workspace_also_when_another_connection_is_shown_by_then()
    {
        _app.KundenDetails = Kunden;
        var test = await _app.OpenAsync(TestApp.Profile("ERP Test"));
        var id = test.Workspaces.Active!.Id;
        await Lifecycle.StartDataDiffAsync(id);
        _app.Shell.OpenDataDiff(_app.WorkspaceOf(test));
        var dev = await _app.OpenAsync(TestApp.Profile("ERP Dev", ConnectionKind.Dev)); // shown now
        _app.ByKeys = (scn, _, _) => scn is null ? [] : [Row(12, "deleted since")];
        var diff = new TableDiff(Kunden, [], [Diff(DiffStatus.Deleted, 12)], false, new HashSet<int>(), RowKeyKind.PrimaryKey, false);

        await Lifecycle.RestoreAsync(id, diff, diff.Rows);

        var tab = Assert.Single(_app.WorkspaceOf(test).TableTabs);
        Assert.Equal(1, tab.Changes!.PendingCount);
        Assert.Empty(_app.WorkspaceOf(dev).TableTabs);
    }

    /// <summary>Review of WP-78: setting back ran into a commit – its changes were cleared silently or committed unseen.</summary>
    [Fact]
    public async Task Rows_are_not_set_back_while_the_workspace_writes_also_when_it_starts_during_the_read()
    {
        _app.KundenDetails = Kunden;
        var scope = await _app.OpenAsync(TestApp.Profile("ERP Test"));
        var id = scope.Workspaces.Active!.Id;
        var tab = ActiveKunden(scope);
        await Lifecycle.StartDataDiffAsync(id);
        _app.Shell.OpenDataDiff(_app.WorkspaceOf(scope));
        var diff = new TableDiff(Kunden, [], [Diff(DiffStatus.Deleted, 12)], false, new HashSet<int>(), RowKeyKind.PrimaryKey, false);
        var readArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readHeld = new TaskCompletionSource();
        _app.ByKeys = (scn, _, _) =>
        {
            if (scn is null)
            {
                readArrived.TrySetResult();
                readHeld.Task.Wait(Wait.Limit); // the read over the VPN, while the user commits
                return [];
            }

            return [Row(12, "deleted since")];
        };
        var commitReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var commitHeld = new TaskCompletionSource();
        _app.OnCommit = () => { commitReached.TrySetResult(); return commitHeld.Task; };

        var restoring = Lifecycle.RestoreAsync(id, diff, diff.Rows);
        await readArrived.Task.WaitAsync(Wait.Limit, Ct);
        var commit = _app.Editing.CommitAsync(_app.WorkspaceOf(scope));
        await commitReached.Task.WaitAsync(Wait.Limit, Ct);
        Assert.Equal(UI.Resources.ShellText.Restore_Busy, Lifecycle.RestoreUnavailable(id, diff, diff.Rows));
        readHeld.SetResult();
        await restoring.WaitAsync(Wait.Limit, Ct);

        Assert.Null(tab.Changes);
        Assert.Equal(UI.Resources.ShellText.Restore_Busy, _app.Shell.Notice?.Text);
        commitHeld.SetResult();
        await commit;
    }

    [Fact]
    public async Task A_time_travel_tab_shows_its_data_only_and_a_failed_check_leaves_it_unchecked()
    {
        var scope = await _app.OpenAsync(TestApp.Profile("ERP Test"));
        ActiveKunden(scope);
        var tab = _app.Shell.OpenTimeTravel(scope.Workspaces.Active!.Id, TestApp.Kunden, [], [], new TimeTravelState(4000, _app.Time.GetLocalNow(), "FREE/FREEPDB1"))!;

        tab.Mode = TabMode.Structure; // the menu's views, the designer: structure and grants are of now (review of WP-78)
        Assert.Equal(TabMode.Data, tab.Mode);
        // Second review: the tab menu's "SQL" reads what the tab shows, also without filters or a loaded structure.
        Assert.StartsWith("SELECT *\n  FROM KUNDEN AS OF SCN 4000;", ObjectMenuFacts.SqlOf(TestApp.Kunden, TestApp.Profile("ERP Test"), tab).Sql, StringComparison.Ordinal);

        tab.PointChecked = false;
        _app.ScnRead = () => throw new DatabaseException("connection reset", "ORA-12537");
        await Lifecycle.CheckTimeTravelAsync(tab);
        Assert.False(tab.PointChecked); // review of WP-78: a failed check counted as passed
        _app.ScnRead = null;
        await Lifecycle.CheckTimeTravelAsync(tab);
        Assert.True(tab.PointChecked);
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
