using FerretSharp.Core.Connections;
using FerretSharp.Core.Schema;
using FerretSharp.Core.Tests.Fakes;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace FerretSharp.Core.Tests.Connections;

public sealed class ActiveConnectionTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ferret-tests", Guid.NewGuid().ToString("N"));
    private readonly InMemorySecretStore _secrets = new();
    private readonly IDatabaseConnector _connector = Substitute.For<IDatabaseConnector>();
    private readonly IDatabaseConnection _connection = Substitute.For<IDatabaseConnection>();
    private readonly ISchemaReader _reader = Substitute.For<ISchemaReader>();
    private readonly RecentConnections _recent;
    private readonly ActiveConnection _active;
    private readonly ConnectionProfile _profile = TestProfiles.HostPort();

    public ActiveConnectionTests()
    {
        _recent = new RecentConnections(Path.Combine(_directory, "recent.json"));
        _active = new ActiveConnection(new ConnectionManager(Substitute.For<IConnectionStore>(), _secrets), _connector, _recent);

        _secrets.SetPassword(_profile.Id, "pw");
        _connection.ServerVersion.Returns("23.26.3.0.0");
        _connection.Schema.Returns(_reader);
        _reader.GetTablesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns([new TableSummary("APP_USER", "KUNDEN", TableKind.Table)]);
        _reader.GetSynonymTargetsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns([]);
        _reader.GetForeignKeysAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns([]);
        _connector.OpenAsync(Arg.Any<ConnectionProfile>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_connection);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Connect_opens_session_loads_schema_of_effective_owner_and_records_usage()
    {
        var statuses = new List<ConnectionStatus>();
        _active.Changed += () => statuses.Add(_active.Status);

        await _active.ConnectAsync(_profile, Ct);

        Assert.Equal([ConnectionStatus.Connecting, ConnectionStatus.Connected], statuses);
        Assert.Equal("APP_USER", _active.Schema!.Owner);
        Assert.Single(_active.Schema.Tables);
        Assert.Equal("23.26.3.0.0", _active.ServerVersion);
        await _connector.Received(1).OpenAsync(_profile, "pw", ActiveConnection.ExplorerAction, Arg.Any<CancellationToken>());
        Assert.True(_recent.LastUsed.ContainsKey(_profile.Id));
    }

    [Fact]
    public async Task Oracle_failure_ends_in_failed_status_with_error_code()
    {
        _connector.OpenAsync(Arg.Any<ConnectionProfile>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Throws(new DatabaseException("invalid credential", "ORA-01017"));

        await _active.ConnectAsync(_profile, Ct);

        Assert.Equal(ConnectionStatus.Failed, _active.Status);
        Assert.Equal(new ConnectionError("invalid credential", "ORA-01017"), _active.Error);
        Assert.Same(_profile, _active.Profile);
        Assert.Null(_active.Schema);
    }

    [Fact]
    public async Task Missing_password_fails_without_contacting_the_database()
    {
        _secrets.DeletePassword(_profile.Id);

        await _active.ConnectAsync(_profile, Ct);

        Assert.Equal(ConnectionStatus.Failed, _active.Status);
        Assert.Contains("Passwort", _active.Error!.Message);
        await _connector.DidNotReceiveWithAnyArgs().OpenAsync(default!, default!, default!, Ct);
    }

    [Fact]
    public async Task Schema_load_failure_closes_the_session()
    {
        _reader.GetTablesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Throws(new DatabaseException("no access", "ORA-00942"));

        await _active.ConnectAsync(_profile, Ct);

        Assert.Equal(ConnectionStatus.Failed, _active.Status);
        await _connection.Received(1).DisposeAsync();
    }

    [Fact]
    public async Task Disconnect_closes_session_and_clears_state()
    {
        await _active.ConnectAsync(_profile, Ct);

        await _active.DisconnectAsync();

        Assert.Equal(ConnectionStatus.Disconnected, _active.Status);
        Assert.Null(_active.Profile);
        Assert.Null(_active.Schema);
        await _connection.Received(1).DisposeAsync();
    }

    [Fact]
    public async Task Connecting_to_another_profile_closes_the_previous_session()
    {
        var other = TestProfiles.HostPort("Other");
        _secrets.SetPassword(other.Id, "pw2");
        await _active.ConnectAsync(_profile, Ct);

        await _active.ConnectAsync(other, Ct);

        Assert.Same(other, _active.Profile);
        await _connection.Received(1).DisposeAsync();
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
