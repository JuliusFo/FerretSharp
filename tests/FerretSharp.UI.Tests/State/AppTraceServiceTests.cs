using System.Runtime.CompilerServices;
using System.Threading.Channels;
using FerretSharp.Core.AppTrace;
using FerretSharp.Core.Connections;
using FerretSharp.Testing;
using FerretSharp.UI.State;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace FerretSharp.UI.Tests.State;

/// <summary>WP-33: the state of the "App statements" page – processes, attaching, the recording, the app's connection.</summary>
public sealed class AppTraceServiceTests : IAsyncDisposable
{
    private static readonly TraceableProcess ErpApi = new(4711, "Erp.Api", @"C:\Apps\Erp.Api.exe -environment=erp-test", null);
    private static readonly TraceableProcess Worker = new(4712, "Erp.Worker", null, null);

    private readonly FakeTraceSource _source = new();
    private readonly FakeTimeProvider _time = new();
    private readonly IConnectionStore _store = Substitute.For<IConnectionStore>();
    private readonly ConnectionManager _connections;
    private readonly AppTraceService _service;
    private int _changed;

    public AppTraceServiceTests()
    {
        _connections = new ConnectionManager(_store, new InMemorySecretStore());
        _service = new AppTraceService(_source, _connections, NullLogger<AppTraceService>.Instance, _time);
        _service.Changed += () => Interlocked.Increment(ref _changed);
    }

    public ValueTask DisposeAsync() => _service.DisposeAsync();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static EfEvent Executed(string text, string source = "LinqQuery", string dataSource = "db-test:1521/ERPTEST") =>
        new(EfEvents.CommandExecuted, new Dictionary<string, string>
        {
            [EfEvents.Text] = text, [EfEvents.Source] = source, [EfEvents.Method] = "ExecuteReader",
            [EfEvents.Duration] = "00:00:00.0010000", [EfEvents.Context] = "c:0", [EfEvents.DataSource] = dataSource,
        }, DateTimeOffset.UtcNow);

    private static ConnectionProfile Profile(string name, string? environment = null, string service = "ERPTEST") =>
        new(Guid.NewGuid(), name, ConnectionKind.Test, new HostPortAddress("db-test", 1521, service, null), "app", null, false, AppEnvironment: environment);

    /// <summary>Waits until the pump (on the thread pool) got that far.</summary>
    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++)
        {
            await Task.Delay(10, Ct);
        }

        Assert.True(condition());
    }

    [Fact]
    public async Task Processes_are_listed_on_request_with_their_environment()
    {
        _source.Processes = [ErpApi, Worker];
        Assert.Empty(_service.Processes);

        await _service.RefreshProcessesAsync();

        Assert.Equal([ErpApi, Worker], _service.Processes);
        Assert.Equal("erp-test", _service.Processes[0].Environment);
        Assert.False(_service.ListingProcesses);
        Assert.True(_changed >= 2); // started and done
    }

    [Fact]
    public async Task A_failing_list_says_why()
    {
        _source.ListError = new InvalidOperationException("no access");

        await _service.RefreshProcessesAsync();

        Assert.Contains("no access", _service.ListError);
    }

    [Fact]
    public async Task Events_are_recorded_and_reported_at_most_every_render_interval()
    {
        await _service.AttachAsync(ErpApi);
        Assert.Equal((TraceStatus.Attaching, ErpApi), (_service.Status, _service.Process));

        for (var i = 0; i < 100; i++)
        {
            await _source.Events.Writer.WriteAsync(Executed("SELECT 1 FROM DUAL"), Ct);
        }

        await Until(() => _service.Snapshot.Count == 100);
        Assert.Equal(TraceStatus.Recording, _service.Status);
        var before = _changed;
        _time.Advance(AppTraceService.RenderInterval);
        Assert.Equal(before + 1, _changed); // one report for all of them
        _time.Advance(AppTraceService.RenderInterval);
        Assert.Equal(before + 1, _changed); // nothing new, nothing reported
    }

    /// <summary>R5b (review 3.31.0, B10): the list of the page is computed once per recording, filter and order – not on every render.</summary>
    [Fact]
    public async Task The_shown_statements_follow_filter_and_order_and_are_kept_until_they_change()
    {
        await _service.AttachAsync(ErpApi);
        await _source.Events.Writer.WriteAsync(Executed("SELECT 1 FROM DUAL"), Ct);
        await _source.Events.Writer.WriteAsync(Executed("SELECT 2 FROM DUAL", source: "FromSqlQuery"), Ct);
        await _source.Events.Writer.WriteAsync(Executed("SELECT 2 FROM DUAL", source: "FromSqlQuery"), Ct);
        await Until(() => _service.Snapshot.Count == 3);

        var byTime = _service.ShownStatements;
        Assert.Same(byTime, _service.ShownStatements);
        Assert.Equal(_service.Snapshot.Statements, byTime);

        _service.Sort = StatementSort.Count;
        Assert.Equal(["SELECT 2 FROM DUAL", "SELECT 1 FROM DUAL"], _service.ShownStatements.Select(s => s.Text));

        _service.OriginFilter = StatementOrigin.Linq;
        Assert.Equal(["SELECT 1 FROM DUAL"], _service.ShownStatements.Select(s => s.Text));

        await _source.Events.Writer.WriteAsync(Executed("SELECT 3 FROM DUAL"), Ct);
        await Until(() => _service.Snapshot.Count == 4);
        Assert.Equal(2, _service.ShownStatements.Count);
    }

    [Fact]
    public async Task When_the_app_ends_the_recording_stays()
    {
        await _service.AttachAsync(ErpApi);
        await _source.Events.Writer.WriteAsync(Executed("SELECT 1 FROM DUAL"), Ct);

        _source.Events.Writer.Complete();

        await Until(() => _service.Status == TraceStatus.Ended);
        Assert.Equal(1, _service.Snapshot.Count);
    }

    [Fact]
    public async Task Detaching_stops_reading_and_keeps_the_recording()
    {
        await _service.AttachAsync(ErpApi);
        await _source.Events.Writer.WriteAsync(Executed("SELECT 1 FROM DUAL"), Ct);
        await Until(() => _service.Snapshot.Count == 1);

        await _service.DetachAsync();
        await _source.Events.Writer.WriteAsync(Executed("SELECT 2 FROM DUAL"), Ct);

        Assert.Equal(TraceStatus.Detached, _service.Status);
        Assert.True(_source.Canceled);
        Assert.Equal(1, _service.Snapshot.Count);
    }

    [Fact]
    public async Task A_refused_attach_says_why()
    {
        _source.AttachError = new RefusedException("The process is gone.");

        await _service.AttachAsync(ErpApi);

        await Until(() => _service.Status == TraceStatus.Failed);
        Assert.Equal("The process is gone.", _service.TraceError);
    }

    [Fact]
    public async Task Attaching_again_starts_a_new_recording_and_reset_clears_it()
    {
        await _service.AttachAsync(ErpApi);
        await _source.Events.Writer.WriteAsync(Executed("SELECT 1 FROM DUAL"), Ct);
        await Until(() => _service.Snapshot.Count == 1);

        await _service.AttachAsync(Worker);
        Assert.Equal((0, Worker), (_service.Snapshot.Count, _service.Process));
        await _source.Events.Writer.WriteAsync(Executed("SELECT 2 FROM DUAL"), Ct);
        await Until(() => _service.Snapshot.Count == 1);

        _service.Reset();

        Assert.Equal(0, _service.Snapshot.Count);
        Assert.Equal(TraceStatus.Recording, _service.Status);
    }

    [Fact]
    public async Task Pausing_stays_attached_and_leaves_out_new_statements_until_resumed()
    {
        await _service.AttachAsync(ErpApi);
        var first = Executed("SELECT 1 FROM DUAL");
        await _source.Events.Writer.WriteAsync(first with { Fields = new Dictionary<string, string>(first.Fields) { [EfEvents.CommandId] = "a" } }, Ct);
        await Until(() => _service.Snapshot.Count == 1);

        _service.SetPaused(true);
        await _source.Events.Writer.WriteAsync(Executed("SELECT 2 FROM DUAL"), Ct);
        var closed = new Dictionary<string, string> { [EfEvents.CommandId] = "a", [EfEvents.ReadCount] = "3", [EfEvents.Duration] = "00:00:00" };
        await _source.Events.Writer.WriteAsync(new EfEvent(EfEvents.DataReaderDisposing, closed, DateTimeOffset.UtcNow), Ct);
        await Until(() => _service.Snapshot.Statements is [{ Rows: 2 }]); // its reader still counts, SELECT 2 was left out
        _service.Reset();
        _service.SetPaused(false);
        await _source.Events.Writer.WriteAsync(Executed("SELECT 3 FROM DUAL"), Ct);
        await Until(() => _service.Snapshot.Count == 1);

        Assert.Equal("SELECT 3 FROM DUAL", Assert.Single(_service.Snapshot.Statements).Text);
        Assert.Equal((TraceStatus.Recording, false), (_service.Status, _source.Canceled));

        _service.SetPaused(true);
        await _service.AttachAsync(Worker);
        Assert.False(_service.Paused); // a new trace records from the start
    }

    [Fact]
    public async Task The_connection_is_found_by_environment_then_chosen_and_remembered()
    {
        var test = Profile("ERP Test");
        var testReader = Profile("ERP Test (reader)");
        await _connections.SaveAsync(test, "pw", Ct);
        await _connections.SaveAsync(testReader, "pw", Ct);
        await _service.AttachAsync(ErpApi);
        await _source.Events.Writer.WriteAsync(Executed("SELECT 1 FROM DUAL"), Ct);
        await Until(() => _service.Snapshot.Count == 1);

        var ambiguous = _service.Match; // same database, no environment at the connections yet
        Assert.Equal((null, 2), (ambiguous.Profile, ambiguous.Candidates.Count));

        _service.Choose(testReader);
        Assert.Equal((testReader, MatchKind.Chosen), (_service.Match.Profile, _service.Match.Kind));

        await _service.RememberEnvironmentAsync(testReader, Ct);

        Assert.Equal("erp-test", _connections.Profiles.Single(p => p.Id == testReader.Id).AppEnvironment);
        Assert.Equal((testReader.Id, MatchKind.Environment), (_service.Match.Profile?.Id, _service.Match.Kind));
    }

    [Fact]
    public async Task Attaching_while_a_slow_stop_is_under_way_leaves_one_trace_that_detach_stops()
    {
        // Review of WP-33: a second attach during a slow stop must not start a trace nothing stops any more.
        var third = new TraceableProcess(4713, "Erp.Jobs", null, null);
        var slow = new TaskCompletionSource();
        _source.SlowStop = slow;
        await _service.AttachAsync(ErpApi);
        await Until(() => _source.Running == 1);

        var toWorker = _service.AttachAsync(Worker); // waits for the stop of the first one
        await Until(() => _service.Switching);
        var toThird = _service.AttachAsync(third);
        slow.SetResult();
        await Task.WhenAll(toWorker, toThird);

        Assert.Equal(third, _service.Process);
        await Until(() => _source.Running == 1);
        Assert.False(_service.Switching);

        await _service.DetachAsync();
        await Until(() => _source.Running == 0);
        Assert.Equal(TraceStatus.Detached, _service.Status);
    }

    [Fact]
    public async Task A_list_refresh_keeps_the_error_of_the_trace()
    {
        _source.AttachError = new RefusedException("No diagnostics port.");
        await _service.AttachAsync(ErpApi);
        await Until(() => _service.Status == TraceStatus.Failed);

        await _service.RefreshProcessesAsync();

        Assert.Equal(("No diagnostics port.", null), (_service.TraceError, _service.ListError));
    }

    [Fact]
    public async Task Remembering_an_environment_another_connection_has_keeps_the_choice()
    {
        var test = Profile("ERP Test", environment: "erp-test");
        var testReader = Profile("ERP Test (reader)");
        await _connections.SaveAsync(test, "pw", Ct);
        await _connections.SaveAsync(testReader, "pw", Ct);
        await _service.AttachAsync(ErpApi);
        _service.Choose(testReader);

        await _service.RememberEnvironmentAsync(testReader, Ct);

        Assert.Equal((testReader.Id, MatchKind.Chosen), (_service.Match.Profile?.Id, _service.Match.Kind)); // both have it now
    }

    [Fact]
    public async Task The_view_state_survives_and_a_new_attach_clears_the_filter()
    {
        await _service.AttachAsync(ErpApi);
        _service.OriginFilter = StatementOrigin.LazyLoading;
        _service.Sort = StatementSort.Count;
        _service.ShowTooling = true;

        await _service.AttachAsync(Worker);

        Assert.Equal((null, StatementSort.Count, true), (_service.OriginFilter, _service.Sort, _service.ShowTooling));
    }

    private sealed class FakeTraceSource : IAppTraceSource
    {
        private int _running;

        public IReadOnlyList<TraceableProcess> Processes { get; set; } = [];

        public Exception? ListError { get; set; }

        public Exception? AttachError { get; set; }

        public Channel<EfEvent> Events { get; private set; } = Channel.CreateUnbounded<EfEvent>();

        public bool Canceled { get; private set; }

        /// <summary>Set: a canceled trace ends only once this completes (an app halted at a breakpoint).</summary>
        public TaskCompletionSource? SlowStop { get; set; }

        /// <summary>Traces started and not ended yet.</summary>
        public int Running => Volatile.Read(ref _running);

        public IReadOnlyList<TraceableProcess> ListProcesses() => ListError is { } error ? throw error : Processes;

        public async IAsyncEnumerable<EfEvent> AttachAsync(int processId, [EnumeratorCancellation] CancellationToken ct)
        {
            if (AttachError is { } error)
            {
                throw error;
            }

            Interlocked.Increment(ref _running);
            try
            {
                var events = Events.Reader;
                while (true)
                {
                    EfEvent e;
                    try
                    {
                        if (!await events.WaitToReadAsync(ct) || !events.TryRead(out e!))
                        {
                            break;
                        }
                    }
                    catch (ChannelClosedException)
                    {
                        break;
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        break;
                    }

                    yield return e;
                }

                if (ct.IsCancellationRequested && SlowStop is { } slow)
                {
                    await slow.Task;
                }
            }
            finally
            {
                // Not through ct.Register: canceling ends the wait above first, and the loop can dispose that registration
                // before its callback ran (flaky under load). The end of the trace is what the service waits for.
                Canceled |= ct.IsCancellationRequested;
                Interlocked.Decrement(ref _running);
            }
        }
    }
}
