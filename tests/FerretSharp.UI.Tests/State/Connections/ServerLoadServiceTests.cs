using FerretSharp.Core.Connections;
using FerretSharp.Core.ServerLoad;
using FerretSharp.Testing;
using FerretSharp.UI.State;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace FerretSharp.UI.Tests.State;

/// <summary>WP-75: the server load page looks at the server on a session of its own, every interval, only while it is shown.</summary>
public sealed class ServerLoadServiceTests : IAsyncDisposable
{
    private static readonly DateTime T0 = new(2026, 11, 7, 10, 0, 0);

    private readonly InMemorySecretStore _secrets = new();
    private readonly CountingTimeProvider _time = new();
    private readonly IServerLoadReader _reader = Substitute.For<IServerLoadReader>();
    private readonly IDatabaseConnection _connection = FakeDatabase.Connection();
    private readonly IDatabaseConnector _connector;
    private readonly ServerLoadService _service;
    private readonly ConnectionProfile _profile = Profile("Prod");
    private DateTime _serverTime = T0;

    public ServerLoadServiceTests()
    {
        _secrets.SetPassword(_profile.Id, "secret");
        _connection.ServerLoad.Returns(_reader);
        _connector = FakeDatabase.Connector(_ => _connection);
        _reader.ReadEnvironmentAsync(Arg.Any<CancellationToken>()).Returns(new ServerEnvironment(0, 1, 4));
        _reader.ReadCountersAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            _serverTime = _serverTime.AddSeconds(30);
            return new LoadCounters(_serverTime, T0, 4, 0, 0, 0, 0, 0, 0, 0, new Dictionary<string, long>());
        });
        _reader.ReadStatementsAsync(Arg.Any<DateTime?>(), Arg.Any<CancellationToken>()).Returns([]);
        _reader.ReadActiveSessionsAsync(Arg.Any<CancellationToken>()).Returns([]);
        _service = new ServerLoadService(new ConnectionManager(Substitute.For<IConnectionStore>(), _secrets), _connector,
            NullLogger<ServerLoadService>.Instance, _time);
    }

    public ValueTask DisposeAsync() => _service.DisposeAsync();

    private static ConnectionProfile Profile(string name) =>
        new(Guid.NewGuid(), name, ConnectionKind.Prod, new HostPortAddress("db", 1521, "ERP", null), "app", null, false);

    private int Looks => _reader.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IServerLoadReader.ReadCountersAsync));

    /// <summary>The loop waits for its next look (the interval's timer is set).</summary>
    private Task ParkedAsync(int times = 1) =>
        Wait.UntilAsync(() => _time.DueTimes.Count(d => d == ServerLoadService.Interval) >= times, "the loop to wait for the next look");

    [Fact]
    public async Task The_shown_page_looks_on_its_own_session_every_interval_unless_paused_and_leaving_it_closes_the_session()
    {
        await _service.ShowAsync(_profile);
        await ParkedAsync();

        var view = _service.For(_profile.Id);
        Assert.Equal(ServerLoadStatus.Watching, view.Status);
        Assert.Equal(4, view.Environment!.Cpus);
        await _connector.Received(1).OpenAsync(_profile, "secret", ServerLoadService.SessionAction, Arg.Any<CancellationToken>());
        Assert.Equal(1, Looks);

        _time.Advance(ServerLoadService.Interval);
        await ParkedAsync(2);
        Assert.Equal(2, Looks);
        Assert.NotNull(view.Tracker.Latest);

        _service.SetPaused(_profile.Id, true);
        var lastLook = view.LastLookAt;
        _time.Advance(ServerLoadService.Interval);
        await ParkedAsync(3);
        Assert.Equal(2, Looks);
        Assert.Equal(lastLook, view.LastLookAt); // review of WP-75: paused, "As of" stays at the latest real look

        await _service.RefreshAsync(_profile); // while paused: one look
        await ParkedAsync(4);
        Assert.Equal(3, Looks);
        Assert.NotEqual(lastLook, view.LastLookAt);

        await _service.HideAsync(_profile.Id);

        await _connection.Received(1).DisposeAsync();
        Assert.Equal(ServerLoadStatus.Idle, view.Status);
        Assert.NotNull(view.Tracker.LastLookAt); // what was seen stays for the next visit
    }

    [Fact]
    public async Task Without_rights_the_page_says_what_is_missing_and_tries_again_only_on_refresh()
    {
        _reader.ReadEnvironmentAsync(Arg.Any<CancellationToken>())
            .Returns<ServerEnvironment>(_ => throw new ServerLoadUnavailableException("GRANT SELECT_CATALOG_ROLE TO <user>"), _ => new ServerEnvironment(0, 1, 4));

        await _service.ShowAsync(_profile);
        var view = _service.For(_profile.Id);
        await Wait.UntilAsync(() => view.Status == ServerLoadStatus.Unavailable, "the refusal");

        Assert.Equal("GRANT SELECT_CATALOG_ROLE TO APP", view.Error);
        await _connection.Received(1).DisposeAsync();
        Assert.DoesNotContain(ServerLoadService.Interval, _time.DueTimes);

        await _service.RefreshAsync(_profile);
        await ParkedAsync();

        Assert.Equal(ServerLoadStatus.Watching, view.Status);
        Assert.Null(view.Error);
    }

    [Fact]
    public async Task A_failed_look_closes_the_session_and_the_next_one_opens_a_new_session()
    {
        await _service.ShowAsync(_profile);
        await ParkedAsync();
        var view = _service.For(_profile.Id);
        _reader.ReadActiveSessionsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IReadOnlyList<ActiveSession>>(new DatabaseException("ORA-03113: end-of-file on communication channel", "ORA-03113")), Task.FromResult<IReadOnlyList<ActiveSession>>([]));

        _time.Advance(ServerLoadService.Interval);
        await ParkedAsync(2);

        Assert.Equal(ServerLoadStatus.Failed, view.Status);
        Assert.Contains("ORA-03113", view.Error, StringComparison.Ordinal);
        await _connection.Received(1).DisposeAsync();

        _time.Advance(ServerLoadService.Interval);
        await ParkedAsync(3);

        Assert.Equal(ServerLoadStatus.Watching, view.Status);
        await _connector.Received(2).OpenAsync(_profile, "secret", ServerLoadService.SessionAction, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task The_page_of_another_connection_takes_over_and_the_old_page_leaving_late_does_not_stop_it()
    {
        var other = Profile("Test");
        _secrets.SetPassword(other.Id, "secret");
        await _service.ShowAsync(_profile);
        await ParkedAsync();

        await _service.ShowAsync(other); // the new page is built before the old one goes
        await ParkedAsync(2);
        await _service.HideAsync(_profile.Id);
        _time.Advance(ServerLoadService.Interval);
        await ParkedAsync(3);

        Assert.Equal(ServerLoadStatus.Idle, _service.For(_profile.Id).Status);
        Assert.Equal(ServerLoadStatus.Watching, _service.For(other.Id).Status);
        Assert.Equal(3, Looks);
    }

    [Fact]
    public async Task A_refused_login_is_not_tried_again_by_itself()
    {
        _connector.OpenAsync(Arg.Any<ConnectionProfile>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IDatabaseConnection>(new DatabaseException("ORA-01017: invalid credential or not authorized", "ORA-01017")));

        await _service.ShowAsync(_profile);
        var view = _service.For(_profile.Id);
        await Wait.UntilAsync(() => view.Status == ServerLoadStatus.Failed, "the refusal");
        _time.Advance(ServerLoadService.Interval * 3);

        // Review of WP-75: every 30 s a new login would lock the account after FAILED_LOGIN_ATTEMPTS.
        await _connector.Received(1).OpenAsync(_profile, "secret", ServerLoadService.SessionAction, Arg.Any<CancellationToken>());
        Assert.Contains("ORA-01017", view.Error, StringComparison.Ordinal);
        Assert.True(view.Stopped);

        await _service.RefreshAsync(_profile);
        await Wait.UntilAsync(() => _connector.ReceivedCalls().Count() == 2, "the second attempt");
    }

    [Fact]
    public async Task A_look_hanging_on_a_silent_line_gives_up_closes_the_session_and_the_next_look_opens_a_new_one()
    {
        var hang = new TaskCompletionSource<LoadCounters>();
        _connection.DisposeAsync().Returns(_ =>
        {
            hang.TrySetCanceled(); // closing the session ends a call hanging on it (OracleSession.DisposeAsync)
            return ValueTask.CompletedTask;
        });
        await _service.ShowAsync(_profile);
        await ParkedAsync();
        var view = _service.For(_profile.Id);
        _reader.ReadCountersAsync(Arg.Any<CancellationToken>()).Returns(
            hang.Task, Task.FromResult(new LoadCounters(T0.AddHours(1), T0, 4, 0, 0, 0, 0, 0, 0, 0, new Dictionary<string, long>())));

        _time.Advance(ServerLoadService.Interval);
        await Wait.UntilAsync(() => _time.DueTimes.Count(d => d == ServerLoadService.LookTimeout) >= 2, "the second look's limit");
        _time.Advance(ServerLoadService.LookTimeout);
        await ParkedAsync(2);

        Assert.Equal(ServerLoadStatus.Failed, view.Status);
        Assert.Contains("120", view.Error, StringComparison.Ordinal);
        await _connection.Received().DisposeAsync();

        _time.Advance(ServerLoadService.Interval);
        await ParkedAsync(3);
        Assert.Equal(ServerLoadStatus.Watching, view.Status);
        await _connector.Received(2).OpenAsync(_profile, "secret", ServerLoadService.SessionAction, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task The_details_of_a_statement_whose_cursor_is_gone_still_bring_its_whole_text()
    {
        await _service.ShowAsync(_profile);
        await ParkedAsync();
        _connection.Schema.GetCursorByIdAsync("abc", Arg.Any<CancellationToken>()).Returns((Core.Query.CursorInfo?)null);
        _reader.ReadFullTextAsync("abc", Arg.Any<CancellationToken>()).Returns("SELECT the whole text FROM dual");

        await _service.SelectAsync(_profile, "abc");

        var view = _service.For(_profile.Id);
        Assert.Equal("SELECT the whole text FROM dual", view.FullText);
        Assert.False(view.CursorLoading);
        Assert.NotNull(view.CursorError); // not in the shared pool: no figures, no plan
    }

    [Fact]
    public async Task A_lookup_ended_by_closing_the_session_clears_its_spinner()
    {
        await _service.ShowAsync(_profile);
        await ParkedAsync();
        _connection.Schema.GetCursorByIdAsync("abc", Arg.Any<CancellationToken>())
            .Returns(Task.FromException<Core.Query.CursorInfo?>(new OperationCanceledException()));

        await _service.SelectAsync(_profile, "abc");

        var view = _service.For(_profile.Id);
        Assert.False(view.CursorLoading); // review of WP-75: it turned until the statement was clicked twice
        Assert.Null(view.CursorError);
    }
}
