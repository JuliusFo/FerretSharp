using FerretSharp.Core.ServerLoad;

namespace FerretSharp.Core.Tests.ServerLoad;

public sealed class ServerLoadTrackerTests
{
    private static readonly DateTime T0 = new(2026, 11, 7, 10, 0, 0);
    private static readonly DateTime Startup = new(2026, 11, 1, 6, 0, 0);

    internal static LoadCounters Counters(
        DateTime at, long dbTime = 0, long dbCpu = 0, long busy = 0, long idle = 0, long executions = 0,
        IReadOnlyDictionary<string, long>? waits = null, DateTime? startedAt = null) =>
        new(at, startedAt ?? Startup, 4, busy, idle, dbTime, dbCpu, executions, executions * 10, executions / 10, waits ?? new Dictionary<string, long>());

    internal static StatementCounters Cursor(string sqlId, long executions, long cpu, long plan = 1, long gets = 0) =>
        new(sqlId, plan, 0, new StatementFigures(executions, cpu, cpu * 2, gets, 0, executions, executions, 0, 0, 0));

    /// <summary>A tracker with a baseline at <see cref="T0"/>.</summary>
    private static ServerLoadTracker Started(params StatementCounters[] cursors)
    {
        var tracker = new ServerLoadTracker();
        tracker.AddLoad(Counters(T0));
        tracker.Rebase(Counters(T0), cursors);
        return tracker;
    }

    /// <summary>The next look, <paramref name="seconds"/> after the latest one, with the cursors active since.</summary>
    private static void Look(ServerLoadTracker tracker, double seconds, params StatementCounters[] changed)
    {
        tracker.AddLoad(Counters(tracker.LastLookAt!.Value.AddSeconds(seconds)));
        tracker.AddStatements(changed);
    }

    [Fact]
    public void The_load_between_two_looks_is_an_average_per_second()
    {
        var tracker = new ServerLoadTracker();
        Assert.Null(tracker.AddLoad(Counters(T0, dbTime: 1_000_000, dbCpu: 500_000, busy: 100, idle: 300, executions: 1000,
            waits: new Dictionary<string, long> { ["User I/O"] = 0 })));

        // 30 s: 90 s DB time → 3 active sessions, 30 s DB CPU → 1 on CPU; busy 300 cs of 1200 → 25 %.
        var sample = tracker.AddLoad(Counters(T0.AddSeconds(30), dbTime: 91_000_000, dbCpu: 30_500_000, busy: 400, idle: 1200, executions: 7000,
            waits: new Dictionary<string, long> { ["User I/O"] = 45_000_000, ["Concurrency"] = 3_000_000 }))!;

        Assert.Equal(3, sample.ActiveSessions, 6);
        Assert.Equal(1, sample.CpuSessions, 6);
        Assert.Equal(1.0 / 3, sample.DbCpuShare!.Value, 6);
        Assert.Equal(0.25, sample.HostCpu!.Value, 6);
        Assert.Equal(200, sample.ExecutionsPerSecond, 6);
        Assert.Equal(1.5, sample.WaitingSessions["User I/O"], 6);
        Assert.Equal(0.1, sample.WaitingSessions["Concurrency"], 6); // a class new in this look counts from 0
        Assert.Equal("User I/O", sample.TopWaitClass!.Value.Key);
        Assert.False(sample.AfterGap);
        Assert.Equal([sample], tracker.History);
    }

    [Theory]
    [InlineData(false, 5_000)] // counters smaller than before: the instance began again
    [InlineData(true, 2_000_000)] // another startup time
    public void After_a_restart_of_the_instance_there_is_no_sample_and_the_cursors_need_a_new_baseline(bool otherStartup, long dbTime)
    {
        var tracker = Started();
        tracker.AddLoad(Counters(T0.AddSeconds(30), dbTime: 1_000_000, executions: 100));

        var sample = tracker.AddLoad(Counters(T0.AddSeconds(60), dbTime: dbTime, executions: 5000,
            startedAt: otherStartup ? Startup.AddDays(6) : null));

        Assert.Null(sample);
        Assert.True(tracker.NeedsBaseline);
    }

    [Fact]
    public void A_cursor_counts_what_it_did_since_the_look_before_and_a_cursor_loaded_meanwhile_from_zero()
    {
        var tracker = Started(Cursor("old", executions: 100, cpu: 1000), Cursor("reloaded", executions: 50, cpu: 900), Cursor("idle", 7, 70));

        Look(tracker, 30,
            Cursor("old", executions: 110, cpu: 1600), // +10, +600
            Cursor("reloaded", executions: 3, cpu: 30), // smaller than held: loaded again, counts from 0
            Cursor("new", executions: 1, cpu: 5_000_000), // not in the baseline: loaded after it – one expensive run
            Cursor("idle", 7, 70)); // read again without change

        var top = tracker.Top(null, StatementSort.Cpu, StatementFilter.None, 10);

        Assert.Equal(["new", "old", "reloaded"], top.Select(r => r.SqlId));
        Assert.Equal((10, 600), (top[1].Figures.Executions, top[1].Figures.CpuTime));
        Assert.Equal((3, 30), (top[2].Figures.Executions, top[2].Figures.CpuTime));
        Assert.Equal(T0.AddSeconds(30), top[0].LastActive);
    }

    [Fact]
    public void The_plans_of_a_statement_add_up_and_are_counted()
    {
        var tracker = Started(Cursor("a", 10, 100, plan: 1), Cursor("a", 10, 100, plan: 2));

        Look(tracker, 30, Cursor("a", 15, 150, plan: 1), Cursor("a", 12, 120, plan: 2));

        var row = Assert.Single(tracker.Top(null, StatementSort.Executions, StatementFilter.None, 10));
        Assert.Equal((7, 70, 2), (row.Figures.Executions, row.Figures.CpuTime, row.Plans));
    }

    [Fact]
    public void A_window_counts_only_the_looks_lying_in_it_completely()
    {
        var tracker = Started(Cursor("early", 0, 0), Cursor("late", 0, 0));
        Look(tracker, 30, Cursor("early", 1, 900));
        Look(tracker, 600, Cursor("late", 1, 100)); // after a gap of 10 min: spans more than the 5-min window
        Look(tracker, 30, Cursor("late", 2, 200));

        Assert.Equal([("late", 100L)], tracker.Top(TimeSpan.FromMinutes(5), StatementSort.Cpu, StatementFilter.None, 10).Select(r => (r.SqlId, r.Figures.CpuTime)));
        Assert.Equal([("late", 200L)], tracker.Top(TimeSpan.FromSeconds(640), StatementSort.Cpu, StatementFilter.None, 10).Select(r => (r.SqlId, r.Figures.CpuTime)));
        Assert.Equal([("early", 900L), ("late", 200L)], tracker.Top(null, StatementSort.Cpu, StatementFilter.None, 10).Select(r => (r.SqlId, r.Figures.CpuTime)));
        Assert.True(tracker.History[1].AfterGap);
    }

    [Fact]
    public void Sorted_per_execution_a_statement_not_executed_yet_comes_last_and_ties_go_by_sql_id()
    {
        var tracker = Started(Cursor("b", 0, 0), Cursor("a", 0, 0), Cursor("c", 0, 0), Cursor("z", 0, 0));
        Look(tracker, 30,
            Cursor("b", 2, 400), // 200 per execution
            Cursor("a", 4, 800), // 200 per execution
            Cursor("c", 0, 5000), // still running, not executed to the end: 0
            Cursor("z", 1, 300));

        var top = tracker.Top(null, StatementSort.CpuPerExecution, StatementFilter.None, 3);

        Assert.Equal(["z", "a", "b"], top.Select(r => r.SqlId));
    }

    [Fact]
    public void A_filter_matches_parts_of_schema_and_module_and_never_a_statement_without_info()
    {
        var tracker = Started(Cursor("app", 0, 0), Cursor("batch", 0, 0), Cursor("unknown", 0, 0));
        Look(tracker, 30, Cursor("app", 1, 10), Cursor("batch", 1, 20), Cursor("unknown", 1, 30));
        tracker.AddInfo(["app", "batch", "unknown"],
            [new StatementInfo("app", "SELECT 1", "Shop.Api", "SHOP"), new StatementInfo("batch", "SELECT 2", "SQL*Plus", "SHOP_BATCH")]);

        Assert.Equal(["app"], tracker.Top(null, StatementSort.Cpu, new StatementFilter(" shop ", "API"), 10).Select(r => r.SqlId));
        Assert.Equal(["batch", "app"], tracker.Top(null, StatementSort.Cpu, new StatementFilter("shop"), 10).Select(r => r.SqlId));
        Assert.Equal(3, tracker.Top(null, StatementSort.Cpu, new StatementFilter(" ", ""), 10).Count);
    }

    [Fact]
    public void Info_is_asked_for_once_per_statement_the_most_expensive_first_even_if_it_was_gone()
    {
        var tracker = Started(Cursor("cheap", 0, 0), Cursor("dear", 0, 0), Cursor("mid", 0, 0));
        Look(tracker, 30, Cursor("cheap", 1, 1), Cursor("dear", 1, 100), Cursor("mid", 1, 50));

        Assert.Equal(["dear", "mid"], tracker.SqlIdsWithoutInfo(2));

        tracker.AddInfo(["dear", "mid"], [new StatementInfo("dear", "SELECT", null, "APP")]); // "mid" left the shared pool

        Assert.Equal(["cheap"], tracker.SqlIdsWithoutInfo(10));
        Assert.Null(tracker.Top(null, StatementSort.Cpu, StatementFilter.None, 10).Single(r => r.SqlId == "mid").Info);
    }
}
