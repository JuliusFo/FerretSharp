using FerretSharp.Core.Connections;
using FerretSharp.Core.Data;
using FerretSharp.Testing;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace FerretSharp.Core.Tests.Connections;

/// <summary>The session for reads of the committed state beside the workspaces (WP-44, ADR 0047).</summary>
public sealed class ReadSessionTests
{
    private readonly InMemorySecretStore _secrets = new();
    private readonly FakeTimeProvider _time = new();
    private readonly ConnectionProfile _profile = TestProfiles.HostPort();
    private readonly List<IDatabaseConnection> _opened = [];
    private readonly IDatabaseConnector _connector;
    private readonly ReadSession _reads;

    public ReadSessionTests()
    {
        _connector = FakeDatabase.Connector(_ =>
        {
            var connection = FakeDatabase.Connection();
            var data = Substitute.For<IDataAccess>();
            data.CurrentScnAsync(Arg.Any<CancellationToken>()).Returns(4711L);
            connection.Data.Returns(data);
            lock (_opened)
            {
                _opened.Add(connection);
            }

            return connection;
        });
        _reads = new ReadSession(new ConnectionManager(Substitute.For<IConnectionStore>(), _secrets), _connector, _time);
        _secrets.SetPassword(_profile.Id, "pw");
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Task<long> ScnAsync() => _reads.ReadAsync((data, ct) => data.CurrentScnAsync(ct), Ct);

    [Fact]
    public async Task Reads_open_one_session_and_it_closes_after_the_idle_time()
    {
        _reads.Attach(_profile);

        Assert.Equal(4711L, await ScnAsync());
        Assert.Equal(4711L, await ScnAsync());

        await _connector.Received(1).OpenAsync(_profile, "pw", ReadSession.Action, Arg.Any<CancellationToken>());
        _time.Advance(ReadSession.IdleTimeout - TimeSpan.FromSeconds(1));
        Assert.True(_reads.HasSession);
        _time.Advance(TimeSpan.FromSeconds(1));
        await Wait.UntilAsync(() => _opened[0].ReceivedCalls().Any(c => c.GetMethodInfo().Name == nameof(IAsyncDisposable.DisposeAsync)), "the idle session to close");
        Assert.False(_reads.HasSession);

        await ScnAsync();
        Assert.Equal(2, _opened.Count); // the next read opens a new one
    }

    [Fact]
    public async Task A_lost_session_is_dropped_and_the_next_read_opens_a_new_one()
    {
        _reads.Attach(_profile);
        await ScnAsync();
        _opened[0].Data.CurrentScnAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new DatabaseException("gone", "ORA-03113"));

        await Assert.ThrowsAsync<DatabaseException>(ScnAsync);

        Assert.False(_reads.HasSession);
        Assert.Equal(4711L, await ScnAsync());
        Assert.Equal(2, _opened.Count);
    }

    /// <summary>Review of WP-44: a comparison of twelve tables tried twelve logins after a refused one – the account locks.</summary>
    [Fact]
    public async Task A_refused_login_is_not_tried_again_until_the_connection_opens_again()
    {
        var attempts = 0;
        _connector.Opens(_ =>
        {
            Interlocked.Increment(ref attempts);
            throw new DatabaseException("invalid username/password", ConnectionManager.WrongPasswordCode);
        });
        _reads.Attach(_profile);

        await Assert.ThrowsAsync<DatabaseException>(ScnAsync);
        await Assert.ThrowsAsync<RefusedException>(ScnAsync);
        await Assert.ThrowsAsync<RefusedException>(ScnAsync);
        Assert.Equal(1, attempts);

        _connector.Opens(_ => FakeDatabase.Connection());
        _reads.Attach(_profile); // connected again
        await _reads.ReadAsync((_, _) => Task.FromResult(0), Ct);
        Assert.Equal(1, attempts); // the new connector counts no more
    }

    [Fact]
    public async Task On_a_read_only_profile_the_session_runs_in_read_only_snapshots_and_closes_with_the_connection()
    {
        _reads.Attach(_profile with { ReadOnly = true });
        await ScnAsync();

        await _opened[0].Received(1).UseReadOnlySnapshotsAsync(Arg.Any<CancellationToken>());

        await _reads.DetachAsync();
        await Wait.UntilAsync(() => _opened[0].ReceivedCalls().Any(c => c.GetMethodInfo().Name == nameof(IAsyncDisposable.DisposeAsync)), "the session to close");
        Assert.False(_reads.HasSession);
        await Assert.ThrowsAsync<RefusedException>(ScnAsync); // not connected: no session of its own
    }
}
