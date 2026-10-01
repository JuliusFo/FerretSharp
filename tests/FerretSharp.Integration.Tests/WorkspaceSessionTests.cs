using FerretSharp.Core.Connections;
using FerretSharp.Core.Data;
using FerretSharp.Core.Oracle;
using FerretSharp.Core.Query;
using FerretSharp.Core.Schema;
using FerretSharp.Core.Workspaces;
using Oracle.ManagedDataAccess.Client;

namespace FerretSharp.Integration.Tests;

/// <summary>Workspaces against the container: every open workspace browses on its own session named after it.</summary>
public sealed class WorkspaceSessionTests(OracleContainerFixture oracle) : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ferret-it", Guid.NewGuid().ToString("N"));
    private readonly List<OracleSession> _sessionsToClose = [];
    private WorkspaceManager _workspaces = null!;
    private ConnectionProfile _profile = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        var (profile, password) = oracle.RequireProfile();
        _profile = profile;
        await SampleSchema.EnsureCreatedAsync(oracle.RequireConnectionString(), Ct);
        await using (var connection = new OracleConnection(oracle.RequireConnectionString()))
        {
            await connection.OpenAsync(Ct);
            await using var command = connection.CreateCommand();
            command.CommandText =
                "CREATE OR REPLACE VIEW SESSION_INFO AS SELECT SYS_CONTEXT('USERENV', 'ACTION') AS ACTION, SYS_CONTEXT('USERENV', 'SID') AS SID FROM DUAL";
            await command.ExecuteNonQueryAsync(Ct);
        }

        var secrets = new SingleSecretStore(profile.Id, password);
        _workspaces = new WorkspaceManager(
            new WorkspaceStore(_directory), new ConnectionManager(new NoConnectionStore(), secrets), new OracleDatabaseConnector());
        await _workspaces.AttachAsync(profile, Ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_workspaces is not null)
        {
            await _workspaces.DisposeAsync();
        }

        foreach (var session in _sessionsToClose)
        {
            await session.DisposeAsync();
        }

        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private async Task<(string Action, string Sid)> SessionInfoAsync(Guid workspaceId)
    {
        var data = await _workspaces.GetDataAsync(workspaceId, Ct);
        var view = new TableSummary(_profile.EffectiveSchema, "SESSION_INFO", TableKind.View);
        var details = new TableDetails(view,
            [
                new ColumnInfo("ACTION", "VARCHAR2", 64, false, null, null, true, false, null, 1),
                new ColumnInfo("SID", "VARCHAR2", 64, false, null, null, true, false, null, 2),
            ],
            [], [], false);
        var page = await data.ReadPageAsync(details, [], [], new PageSpec(0, 10), Ct);
        var row = Assert.Single(page.Rows);
        return ((string)row.Values[0]!, (string)row.Values[1]!);
    }

    [Fact]
    public async Task Each_workspace_browses_on_its_own_session_named_after_it()
    {
        var first = _workspaces.Active!;
        var second = await _workspaces.CreateAsync("Bug 3711");

        var info1 = await SessionInfoAsync(first.Id);
        var info2 = await SessionInfoAsync(second.Id);

        Assert.Equal("Workspace 1", info1.Action);
        Assert.Equal("Bug 3711", info2.Action);
        Assert.NotEqual(info1.Sid, info2.Sid);
    }

    [Fact]
    public async Task Renaming_a_workspace_renames_its_session()
    {
        var id = _workspaces.Active!.Id;
        var before = await SessionInfoAsync(id);

        _workspaces.Rename(id, "Feature ABC");
        await Task.Delay(100, Ct); // the action is set in the background
        var after = await SessionInfoAsync(id);

        Assert.Equal("Feature ABC", after.Action);
        Assert.Equal(before.Sid, after.Sid);
    }

    [Fact]
    public async Task Two_workspaces_read_the_same_table_with_different_filters_at_the_same_time()
    {
        var first = _workspaces.Active!;
        var second = await _workspaces.CreateAsync();
        var data1 = await _workspaces.GetDataAsync(first.Id, Ct);
        var data2 = await _workspaces.GetDataAsync(second.Id, Ct);
        var schema = new SchemaCache(new OracleSchemaReader(await OpenExplorerSessionAsync()), _profile.EffectiveSchema);
        await schema.LoadAsync(Ct);
        var grid = await schema.GetDetailsAsync(schema.Find(new TableRef(_profile.EffectiveSchema, "GRID_TEST"))!, Ct);

        var pages = await Task.WhenAll(
            data1.ReadPageAsync(grid, [FilterCondition.Of("GRUPPE", FilterOperator.Equals, "1")], [], new PageSpec(0, 2000), Ct),
            data2.ReadPageAsync(grid, [FilterCondition.Of("GRUPPE", FilterOperator.Equals, "0")], [], new PageSpec(0, 2000), Ct));

        Assert.Equal(177, pages[0].Rows.Count); // LEVEL 1..1234 with MOD(LEVEL, 7) = 1
        Assert.Equal(176, pages[1].Rows.Count); // MOD(LEVEL, 7) = 0
    }

    private async Task<OracleSession> OpenExplorerSessionAsync()
    {
        var (profile, password) = oracle.RequireProfile();
        var session = await OracleSession.OpenAsync(
            OracleConnectionStringFactory.Create(profile, password), new SessionContext("FerretSharp", "Explorer"), Ct);
        _sessionsToClose.Add(session);
        return session;
    }

    private sealed class SingleSecretStore(Guid id, string password) : ISecretStore
    {
        public string? GetPassword(Guid profileId) => profileId == id ? password : null;

        public void SetPassword(Guid profileId, string value) => throw new NotSupportedException();

        public void DeletePassword(Guid profileId) => throw new NotSupportedException();
    }

    private sealed class NoConnectionStore : IConnectionStore
    {
        public Task<IReadOnlyList<ConnectionProfile>> LoadAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ConnectionProfile>>([]);

        public Task SaveAsync(IReadOnlyList<ConnectionProfile> profiles, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
