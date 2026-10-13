using System.Runtime.CompilerServices;
using System.Threading.Channels;
using FerretSharp.Core.AppTrace;
using FerretSharp.Core.Connections;
using FerretSharp.Core.Tests.Fakes;
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

        Assert.Contains("no access", _service.Error);
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
        Assert.Equal("The process is gone.", _service.Error);
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

    private sealed class FakeTraceSource : IAppTraceSource
    {
        public IReadOnlyList<TraceableProcess> Processes { get; set; } = [];

        public Exception? ListError { get; set; }

        public Exception? AttachError { get; set; }

        public Channel<EfEvent> Events { get; private set; } = Channel.CreateUnbounded<EfEvent>();

        public bool Canceled { get; private set; }

        public IReadOnlyList<TraceableProcess> ListProcesses() => ListError is { } error ? throw error : Processes;

        public async IAsyncEnumerable<EfEvent> AttachAsync(int processId, [EnumeratorCancellation] CancellationToken ct)
        {
            if (AttachError is { } error)
            {
                throw error;
            }

            var events = Events.Reader;
            using var registration = ct.Register(() => Canceled = true);
            while (true)
            {
                EfEvent e;
                try
                {
                    if (!await events.WaitToReadAsync(ct) || !events.TryRead(out e!))
                    {
                        yield break;
                    }
                }
                catch (ChannelClosedException)
                {
                    yield break;
                }

                yield return e;
            }
        }
    }
}
