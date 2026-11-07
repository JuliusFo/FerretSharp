using FerretSharp.Core.ServerLoad;
using NSubstitute;
using static FerretSharp.Core.Tests.ServerLoad.ServerLoadTrackerTests;

namespace FerretSharp.Core.Tests.ServerLoad;

public sealed class ServerLoadMonitorTests
{
    private static readonly DateTime T0 = new(2026, 11, 7, 10, 0, 0, 250);

    private readonly IServerLoadReader _reader = Substitute.For<IServerLoadReader>();
    private readonly ServerLoadTracker _tracker = new();
    private readonly List<ServerLoadStep> _steps = [];

    public ServerLoadMonitorTests()
    {
        _reader.ReadEnvironmentAsync(Arg.Any<CancellationToken>()).Returns(new ServerEnvironment(0, 1, 8));
        _reader.ReadStatementsAsync(Arg.Any<DateTime?>(), Arg.Any<CancellationToken>()).Returns([]);
        _reader.ReadStatementsAsync(null, Arg.Any<CancellationToken>()).Returns([Cursor("a", 1, 10)]);
        _reader.ReadStatementInfoAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<IReadOnlyList<string>>().Select(id => new StatementInfo(id, "SELECT", null, "APP")).ToList());
        _reader.ReadActiveSessionsAsync(Arg.Any<CancellationToken>())
            .Returns([new ActiveSession(42, 7, "APP", null, null, null, null, null, "a", null, null, TimeSpan.FromSeconds(3), null)]);
    }

    private ServerLoadMonitor Monitor() => new(_reader, _tracker);

    private void CountersAt(params DateTime[] times) =>
        _reader.ReadCountersAsync(Arg.Any<CancellationToken>()).Returns(Counters(times[0]), [.. times.Skip(1).Select(t => Counters(t))]);

    [Fact]
    public async Task The_first_look_checks_the_rights_and_reads_the_baseline_then_looks_only_at_cursors_active_since()
    {
        CountersAt(T0, T0.AddSeconds(30));
        var monitor = Monitor();

        await monitor.OpenAsync(_steps.Add, TestContext.Current.CancellationToken);

        Assert.Equal(8, monitor.Environment!.Cpus);
        Assert.Equal([ServerLoadStep.Baseline], _steps);
        Assert.Equal(42, Assert.Single(monitor.Sessions).Sid);

        _reader.ReadStatementsAsync(Arg.Any<DateTime?>(), Arg.Any<CancellationToken>()).Returns([Cursor("a", 2, 30)]);
        await monitor.LookAsync(_steps.Add, TestContext.Current.CancellationToken);

        // The second of the baseline and the one before it count again: LAST_ACTIVE_TIME has whole seconds.
        await _reader.Received(1).ReadStatementsAsync(new DateTime(2026, 11, 7, 9, 59, 59), Arg.Any<CancellationToken>());
        Assert.Single(_steps);
        var row = Assert.Single(_tracker.Top(null, StatementSort.Cpu, StatementFilter.None, 10));
        Assert.Equal((20, "APP"), (row.Figures.CpuTime, row.Info!.Schema));
    }

    [Fact]
    public async Task A_new_session_on_a_kept_tracker_goes_on_from_the_latest_look_without_a_new_baseline()
    {
        CountersAt(T0, T0.AddMinutes(20));
        await Monitor().OpenAsync(null, TestContext.Current.CancellationToken);

        await Monitor().OpenAsync(_steps.Add, TestContext.Current.CancellationToken);

        Assert.Empty(_steps);
        await _reader.Received(1).ReadStatementsAsync(null, Arg.Any<CancellationToken>());
        Assert.True(Assert.Single(_tracker.History).AfterGap);
    }

    [Fact]
    public async Task After_a_restart_of_the_instance_the_baseline_is_read_again()
    {
        _reader.ReadCountersAsync(Arg.Any<CancellationToken>()).Returns(
            Counters(T0, dbTime: 1_000),
            Counters(T0.AddSeconds(30), dbTime: 10));
        var monitor = Monitor();
        await monitor.OpenAsync(null, TestContext.Current.CancellationToken);

        var sample = await monitor.LookAsync(_steps.Add, TestContext.Current.CancellationToken);

        Assert.Null(sample);
        Assert.Equal([ServerLoadStep.Baseline], _steps);
        await _reader.Received(2).ReadStatementsAsync(null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Text_module_and_schema_are_read_in_batches_and_only_so_many_per_look()
    {
        CountersAt(T0, T0.AddSeconds(30));
        var monitor = Monitor();
        await monitor.OpenAsync(null, TestContext.Current.CancellationToken);
        var changed = Enumerable.Range(0, ServerLoadMonitor.InfoPerLook + 30).Select(i => Cursor($"s{i:000}", 1, i + 1)).ToArray();
        _reader.ReadStatementsAsync(Arg.Any<DateTime?>(), Arg.Any<CancellationToken>()).Returns(changed);

        await monitor.LookAsync(null, TestContext.Current.CancellationToken);

        var asked = _reader.ReceivedCalls().Where(c => c.GetMethodInfo().Name == nameof(IServerLoadReader.ReadStatementInfoAsync))
            .Select(c => ((IReadOnlyList<string>)c.GetArguments()[0]!).Count).ToList();
        Assert.Equal(Enumerable.Repeat(ServerLoadMonitor.InfoBatch, ServerLoadMonitor.InfoPerLook / ServerLoadMonitor.InfoBatch), asked);
        Assert.Equal(30, _tracker.SqlIdsWithoutInfo(1000).Count);
    }
}
