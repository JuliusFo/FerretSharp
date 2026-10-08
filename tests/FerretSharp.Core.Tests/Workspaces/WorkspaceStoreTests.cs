using FerretSharp.Core.Query;
using FerretSharp.Core.Schema;
using FerretSharp.Core.Workspaces;

namespace FerretSharp.Core.Tests.Workspaces;

public sealed class WorkspaceStoreTests : IDisposable
{
    private readonly TestFolder _folder = new();
    private readonly string _directory;
    private readonly WorkspaceStore _store;
    private readonly Guid _connectionId = Guid.NewGuid();

    public WorkspaceStoreTests()
    {
        _directory = _folder.Combine("workspaces"); // created by the store
        _store = new WorkspaceStore(_directory);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Workspace Sample(string name = "Bug 3711") => new(Guid.NewGuid(), _connectionId, name)
    {
        Notes = "Kunde meldet doppelte Rechnung",
        Order = 2,
        LastActive = new DateTimeOffset(2026, 10, 1, 9, 30, 0, TimeSpan.Zero),
        ActiveTabIndex = 1,
        Tabs =
        [
            new TabState(new TableRef("APP", "KUNDEN"), TabMode.Data,
                [FilterCondition.Of("NAME", FilterOperator.Contains, "Müller"), new FilterCondition("ORT", FilterOperator.In, ["Köln", "Bonn"], Enabled: false)],
                [FilterCondition.Of("NAME", FilterOperator.Contains, "Müller")],
                [new SortSpec("NAME", Descending: true)],
                FirstVisibleRow: 1200)
            {
                PinnedColumns = ["NAME", "ORT"],
                Form = new FormTabState(Open: true, Width: 520, HideEmpty: true),
            },
            new TabState(new TableRef("OTHER", "Quoted.Name"), TabMode.Structure, [], [], []) { OriginTab = 0 },
        ],
    };

    [Fact]
    public async Task Round_trips_workspace_with_tabs_filters_and_sorts()
    {
        var workspace = Sample();

        await _store.SaveAsync(workspace, Ct);
        var loaded = Assert.Single((await _store.LoadAsync(_connectionId, Ct)).Workspaces);

        Assert.Equal(
            (workspace.Id, workspace.ConnectionId, workspace.Name, workspace.Notes, workspace.Order, workspace.LastActive, workspace.ActiveTabIndex, workspace.IsOpen),
            (loaded.Id, loaded.ConnectionId, loaded.Name, loaded.Notes, loaded.Order, loaded.LastActive, loaded.ActiveTabIndex, loaded.IsOpen));
        Assert.Equal(2, loaded.Tabs.Count);
        var tab = loaded.Tabs[0];
        Assert.Equal(new TableRef("APP", "KUNDEN"), tab.Table);
        Assert.Equal(TabMode.Data, tab.Mode);
        Assert.Equal(1200, tab.FirstVisibleRow);
        Assert.Equal(["Köln", "Bonn"], tab.FilterRows[1].Values);
        Assert.False(tab.FilterRows[1].Enabled);
        Assert.Equal(FilterOperator.In, tab.FilterRows[1].Op);
        Assert.Equal("Müller", Assert.Single(tab.AppliedFilters).Values[0]);
        Assert.Equal(new SortSpec("NAME", true), Assert.Single(tab.Sorts));
        Assert.Equal(["NAME", "ORT"], tab.PinnedColumns);
        Assert.Null(tab.OriginTab);
        Assert.Equal(0, loaded.Tabs[1].OriginTab);
        Assert.Equal(new TableRef("OTHER", "Quoted.Name"), loaded.Tabs[1].Table);
        Assert.Equal(TabMode.Structure, loaded.Tabs[1].Mode);
        Assert.Null(loaded.Tabs[1].FirstVisibleRow);
        Assert.Empty(loaded.Tabs[1].PinnedColumns);
        Assert.Equal(new FormTabState(true, 520, true), tab.Form);
        Assert.Null(loaded.Tabs[1].Form);
    }

    [Fact]
    public async Task Sql_tabs_keep_script_and_typed_variables()
    {
        var workspace = Sample() with
        {
            Tabs =
            [
                TabState.OfSql(new SqlTabState("SQL 1", "SELECT * FROM kunden WHERE id = :id;\n\nSELECT 1 FROM dual",
                    [new SqlVariable("id", SqlVariableType.Number, "4711"), new SqlVariable("von", SqlVariableType.Date, "01.10.2026")])),
            ],
        };

        await _store.SaveAsync(workspace, Ct);
        var sql = Assert.Single(Assert.Single((await _store.LoadAsync(_connectionId, Ct)).Workspaces).Tabs).Sql;

        Assert.NotNull(sql);
        Assert.Equal(("SQL 1", "SELECT * FROM kunden WHERE id = :id;\n\nSELECT 1 FROM dual"), (sql.Title, sql.Text));
        Assert.Equal(workspace.Tabs[0].Sql!.Variables, sql.Variables);
    }

    [Fact]
    public async Task Tabs_saved_before_column_pinning_load_without_pinned_columns()
    {
        Directory.CreateDirectory(_directory);
        var id = Guid.NewGuid();
        await File.WriteAllTextAsync(Path.Combine(_directory, id + ".json"), $$"""
            { "version": 1, "workspace": { "id": "{{id}}", "connectionId": "{{_connectionId}}", "name": "Alt",
              "tabs": [ { "table": { "owner": "APP", "name": "KUNDEN" }, "mode": "data", "filterRows": [], "appliedFilters": [], "sorts": [] } ] } }
            """, Ct);

        var tab = Assert.Single(Assert.Single((await _store.LoadAsync(_connectionId, Ct)).Workspaces).Tabs);

        Assert.Equal(new TableRef("APP", "KUNDEN"), tab.Table);
        Assert.Empty(tab.PinnedColumns);
        Assert.Null(tab.OriginTab);
        Assert.Null(tab.Form);
    }

    [Fact]
    public async Task Writes_one_file_per_workspace_without_leftover_temp_files()
    {
        var workspace = Sample();

        await _store.SaveAsync(workspace, Ct);
        await _store.SaveAsync(workspace with { Name = "Renamed" }, Ct);

        Assert.Equal([workspace.Id + ".json"], Directory.GetFiles(_directory).Select(Path.GetFileName));
        var json = await File.ReadAllTextAsync(Path.Combine(_directory, workspace.Id + ".json"), Ct);
        Assert.Contains("\"version\": 1", json);
        Assert.Contains("Renamed", json);
    }

    [Fact]
    public async Task Load_returns_only_workspaces_of_the_connection()
    {
        await _store.SaveAsync(Sample("Mine"), Ct);
        await _store.SaveAsync(new Workspace(Guid.NewGuid(), Guid.NewGuid(), "Other connection"), Ct);

        var result = await _store.LoadAsync(_connectionId, Ct);

        Assert.Equal("Mine", Assert.Single(result.Workspaces).Name);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public async Task Missing_directory_means_no_workspaces()
    {
        var result = await _store.LoadAsync(_connectionId, Ct);

        Assert.Empty(result.Workspaces);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public async Task Broken_and_newer_files_are_reported_skipped_and_left_untouched()
    {
        await _store.SaveAsync(Sample("Good"), Ct);
        var broken = Path.Combine(_directory, Guid.NewGuid() + ".json");
        await File.WriteAllTextAsync(broken, "{ not json", Ct);
        var newer = Path.Combine(_directory, Guid.NewGuid() + ".json");
        await File.WriteAllTextAsync(newer, $$"""{ "version": 99, "workspace": { "id": "{{Guid.NewGuid()}}", "connectionId": "{{_connectionId}}", "name": "x" } }""", Ct);

        var result = await _store.LoadAsync(_connectionId, Ct);

        Assert.Equal("Good", Assert.Single(result.Workspaces).Name);
        Assert.Equal(2, result.Errors.Count);
        Assert.Contains(result.Errors, e => e.Contains("kein gültiges Workspace-JSON"));
        Assert.Contains(result.Errors, e => e.Contains("neueren FerretSharp-Version"));
        Assert.Equal("{ not json", await File.ReadAllTextAsync(broken, Ct));
    }

    [Fact]
    public async Task Delete_removes_the_file()
    {
        var workspace = Sample();
        await _store.SaveAsync(workspace, Ct);

        await _store.DeleteAsync(workspace.Id, Ct);

        Assert.Empty((await _store.LoadAsync(_connectionId, Ct)).Workspaces);
    }

    [Fact]
    public async Task Delete_for_connection_keeps_other_connections_and_broken_files()
    {
        var other = new Workspace(Guid.NewGuid(), Guid.NewGuid(), "Other connection");
        await _store.SaveAsync(Sample("A"), Ct);
        await _store.SaveAsync(Sample("B"), Ct);
        await _store.SaveAsync(other, Ct);
        var broken = Path.Combine(_directory, Guid.NewGuid() + ".json");
        await File.WriteAllTextAsync(broken, "{ not json", Ct);

        await _store.DeleteForConnectionAsync(_connectionId, Ct);

        Assert.Empty((await _store.LoadAsync(_connectionId, Ct)).Workspaces);
        Assert.Single((await _store.LoadAsync(other.ConnectionId, Ct)).Workspaces);
        Assert.True(File.Exists(broken));
    }

    [Theory]
    [InlineData("  Bug 3711 ", "Bug 3711")]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    public void Name_is_trimmed_and_must_not_be_empty(string? input, string? expected) =>
        Assert.Equal(expected, Workspace.NormalizeName(input));

    [Fact]
    public void Name_is_limited_in_length()
    {
        Assert.NotNull(Workspace.NormalizeName(new string('x', Workspace.MaxNameLength)));
        Assert.Null(Workspace.NormalizeName(new string('x', Workspace.MaxNameLength + 1)));
    }

    public void Dispose() => _folder.Dispose();
}
