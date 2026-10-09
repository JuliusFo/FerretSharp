using FerretSharp.Core.Connections;
using FerretSharp.Core.Schema;
using FerretSharp.Core.Tests.Fakes;
using FerretSharp.Core.Workspaces;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace FerretSharp.Core.Tests.Connections;

public sealed class ActiveConnectionTests : IDisposable
{
    private readonly TestFolder _folder = new();
    private readonly InMemorySecretStore _secrets = new();
    private readonly IDatabaseConnector _connector = Substitute.For<IDatabaseConnector>();
    private readonly IDatabaseConnection _connection = Substitute.For<IDatabaseConnection>();
    private readonly ISchemaReader _reader = Substitute.For<ISchemaReader>();
    private readonly RecentConnections _recent;
    private readonly InMemoryWorkspaceStore _workspaceStore = new();
    private readonly WorkspaceManager _workspaces;
    private readonly ActiveConnection _active;
    private readonly ConnectionProfile _profile = TestProfiles.HostPort();

    public ActiveConnectionTests()
    {
        _recent = new RecentConnections(_folder.Combine("recent.json"));
        var connections = new ConnectionManager(Substitute.For<IConnectionStore>(), _secrets);
        _workspaces = new WorkspaceManager(_workspaceStore, connections, _connector);
        _active = new ActiveConnection(connections, _connector, _recent, _workspaces);

        _secrets.SetPassword(_profile.Id, "pw");
        _connection.ServerVersion.Returns("23.26.3.0.0");
        _connection.Schema.Returns(_reader);
        _reader.GetTablesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns([new TableSummary("APP_USER", "KUNDEN", TableKind.Table)]);
        _reader.GetSynonymTargetsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(SynonymTargets.None);
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
    public async Task Connect_attaches_the_workspaces_and_disconnect_detaches_them()
    {
        await _active.ConnectAsync(_profile, Ct);

        Assert.Same(_profile, _workspaces.Profile);
        Assert.Single(_workspaces.Open);

        await _active.DisconnectAsync();

        Assert.Null(_workspaces.Profile);
        Assert.Empty(_workspaces.Open);
        Assert.Single(_workspaceStore.Saved);
    }

    [Fact]
    public async Task Connecting_to_another_profile_switches_the_workspaces()
    {
        var other = TestProfiles.HostPort("Other");
        _secrets.SetPassword(other.Id, "pw2");
        await _active.ConnectAsync(_profile, Ct);

        await _active.ConnectAsync(other, Ct);

        Assert.Same(other, _workspaces.Profile);
        Assert.Equal(other.Id, Assert.Single(_workspaces.Open).ConnectionId);
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

    [Fact]
    public async Task Lost_session_is_recorded_once_and_cleared_by_connecting_again()
    {
        await _active.ConnectAsync(_profile, Ct);
        var first = new DatabaseException("connection lost", "ORA-03113");
        var changes = 0;
        _active.Changed += () => changes++;

        Assert.False(_active.ReportLost(new DatabaseException("no such table", "ORA-00942")));
        Assert.True(_active.ReportLost(first));
        Assert.False(_active.ReportLost(new DatabaseException("again", "ORA-03135")));

        Assert.Same(first, _active.Lost);
        Assert.Equal(1, changes);

        await _active.ConnectAsync(_profile, Ct);

        Assert.Null(_active.Lost);
    }

    [Fact]
    public void Lost_session_is_ignored_while_not_connected()
    {
        Assert.False(_active.ReportLost(new DatabaseException("connection lost", "ORA-03113")));
        Assert.Null(_active.Lost);
    }

    [Fact]
    public async Task Clear_lost_removes_the_banner_state_right_away()
    {
        await _active.ConnectAsync(_profile, Ct);
        _active.ReportLost(new DatabaseException("connection lost", "ORA-03113"));

        _active.ClearLost();

        Assert.Null(_active.Lost);
        Assert.True(_active.IsConnected);
    }

    public void Dispose()
    {
        _folder.Dispose();
    }
}
