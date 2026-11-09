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
            data.CurrentPointAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(new DatabasePoint(4711L, "FREE/FREEPDB1"));
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

    private async Task<long> ScnAsync() => (await _reads.ReadAsync((data, ct) => data.CurrentPointAsync(exact: false, ct), Ct)).Scn;

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
        _opened[0].Data.CurrentPointAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>()).ThrowsAsync(new DatabaseException("gone", "ORA-03113"));

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

    /// <summary>
    /// WP-78 (decision of the user): the current SCN exactly from V$DATABASE where the right is there; without it the time is
    /// mapped – and the failing statement is not sent again for every start point, only after connecting anew.
    /// </summary>
    [Fact]
    public async Task The_current_scn_is_read_exactly_where_allowed_and_the_missing_right_is_learned_once_per_connection()
    {
        _reads.Attach(_profile);
        await _reads.ReadAsync((_, _) => Task.FromResult(0), Ct);
        var data = _opened[0].Data;
        data.CurrentPointAsync(true, Arg.Any<CancellationToken>()).ThrowsAsync(new DatabaseException("table or view does not exist", "ORA-00942"));
        data.CurrentPointAsync(false, Arg.Any<CancellationToken>()).Returns(new DatabasePoint(4700L, "FREE/FREEPDB1"));

        Assert.Equal(4700L, (await _reads.CurrentPointAsync(Ct)).Scn);
        Assert.Equal(4700L, (await _reads.CurrentPointAsync(Ct)).Scn);
        await data.Received(1).CurrentPointAsync(true, Arg.Any<CancellationToken>());

        _reads.Attach(_profile); // connected again: the right may have been granted meanwhile
        data.CurrentPointAsync(true, Arg.Any<CancellationToken>()).Returns(new DatabasePoint(4711L, "FREE/FREEPDB1"));
        Assert.Equal(4711L, (await _reads.CurrentPointAsync(Ct)).Scn);
    }

    /// <summary>A time travel tab reads through the read session, each call a read of its own, its table as of the tab's SCN (WP-78).</summary>
    [Fact]
    public async Task The_read_path_as_of_an_scn_reads_on_the_read_session()
    {
        _reads.Attach(_profile);
        var then = Substitute.For<IDataAccess>();
        then.CountAsync(Arg.Any<FerretSharp.Core.Schema.TableDetails>(), Arg.Any<FerretSharp.Core.Query.RowFilter>(), Arg.Any<CancellationToken>()).Returns(3L);
        _connector.Opens(_ =>
        {
            var connection = FakeDatabase.Connection();
            var data = Substitute.For<IDataAccess>();
            data.AsOf(4711L).Returns(then);
            connection.Data.Returns(data);
            return connection;
        });

        var count = await _reads.AsOf(4711).CountAsync(null!, [], Ct);

        Assert.Equal(3L, count);
        Assert.True(_reads.HasSession);
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
