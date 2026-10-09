using FerretSharp.Core.Connections;
using FerretSharp.Core.Schema;
using FerretSharp.Core.Settings;
using FerretSharp.Core.Tests.Fakes;
using FerretSharp.Core.Workspaces;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace FerretSharp.Core.Tests.Connections;

public sealed class ConnectionKeepAliveTests : IDisposable
{
    private readonly TestFolder _folder = new();
    private readonly IDatabaseConnector _connector = Substitute.For<IDatabaseConnector>();
    private readonly IDatabaseConnection _explorer = Substitute.For<IDatabaseConnection>();
    private readonly IDatabaseConnection _workspaceSession = Substitute.For<IDatabaseConnection>();
    private readonly FakeTimeProvider _time = new();
    private readonly WorkspaceManager _workspaces;
    private readonly ActiveConnection _active;
    private readonly AppSettingsService _settings;
    private readonly ConnectionKeepAlive _keepAlive;
    private readonly ConnectionProfile _profile = TestProfiles.HostPort();
    private readonly List<DatabaseException> _lost = [];

    public ConnectionKeepAliveTests()
    {
        var secrets = new InMemorySecretStore();
        secrets.SetPassword(_profile.Id, "pw");
        var connections = new ConnectionManager(Substitute.For<IConnectionStore>(), secrets);
        _workspaces = new WorkspaceManager(new InMemoryWorkspaceStore(), connections, _connector);
        _active = new ActiveConnection(connections, _connector, new RecentConnections(_folder.Combine("recent.json")), _workspaces);
        _settings = new AppSettingsService(new SettingsStore(_folder.Combine("settings.json")), AppSettings.Default);
        var open = Substitute.For<IOpenConnections>();
        open.All.Returns([_active]);
        _keepAlive = new ConnectionKeepAlive(open, _settings, _time);
        _keepAlive.ConnectionLost += (_, error) => _lost.Add(error);

        var reader = Substitute.For<ISchemaReader>();
        reader.GetTablesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns([]);
        reader.GetSynonymTargetsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(SynonymTargets.None);
        reader.GetForeignKeysAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns([]);
        _explorer.Schema.Returns(reader);
        _connector.OpenAsync(Arg.Any<ConnectionProfile>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.ArgAt<string>(2) == ActiveConnection.ExplorerAction ? _explorer : _workspaceSession);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Connected, with the session of the active workspace opened (as after the first grid query).</summary>
    private async Task ConnectAsync()
    {
        await _active.ConnectAsync(_profile, Ct);
        await _workspaces.GetDataAsync(_workspaces.Active!.Id, Ct);
    }

    [Fact]
    public async Task Pings_the_explorer_and_the_open_workspace_sessions_when_idle()
    {
        await ConnectAsync();

        await _keepAlive.TickAsync();

        await _explorer.Received(1).PingIfIdleAsync(ConnectionKeepAlive.IdleFor, Arg.Any<CancellationToken>());
        await _workspaceSession.Received(1).PingIfIdleAsync(ConnectionKeepAlive.IdleFor, Arg.Any<CancellationToken>());
        Assert.Empty(_lost);
    }

    [Fact]
    public async Task Timer_pings_every_interval()
    {
        await ConnectAsync();
        _keepAlive.Start();

        _time.Advance(ConnectionKeepAlive.Interval - TimeSpan.FromSeconds(1));
        await _explorer.DidNotReceive().PingIfIdleAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());

        _time.Advance(TimeSpan.FromSeconds(1));
        _time.Advance(ConnectionKeepAlive.Interval);
        await _explorer.Received(2).PingIfIdleAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Lost_session_is_reported()
    {
        await ConnectAsync();
        var gone = new DatabaseException("Verbindung beendet", "ORA-03135");
        _workspaceSession.PingIfIdleAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Throws(gone);

        await _keepAlive.TickAsync();

        Assert.Same(gone, Assert.Single(_lost));
    }

    [Fact]
    public async Task Other_failures_are_not_reported_and_do_not_stop_the_round()
    {
        await ConnectAsync();
        _explorer.PingIfIdleAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Throws(new DatabaseException("Zeitüberschreitung", "ORA-01013"));

        await _keepAlive.TickAsync();

        Assert.Empty(_lost);
        await _workspaceSession.Received(1).PingIfIdleAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Switched_off_in_the_settings_pings_nothing()
    {
        await ConnectAsync();
        await _settings.UpdateAsync(s => s with { KeepAlive = false }, Ct);

        await _keepAlive.TickAsync();

        await _explorer.DidNotReceive().PingIfIdleAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Without_a_connection_pings_nothing()
    {
        await _keepAlive.TickAsync();

        await _explorer.DidNotReceive().PingIfIdleAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }

    public void Dispose()
    {
        _keepAlive.Dispose();
        _folder.Dispose();
    }
}
