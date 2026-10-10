using FerretSharp.Core.Connections;
using FerretSharp.Core.Data;
using FerretSharp.Core.Query;
using FerretSharp.Core.Schema;
using FerretSharp.UI.State;

namespace FerretSharp.UI.Tests.State;

/// <summary>
/// After DDL (WP-22) and "Schema neu laden" the shell follows the new schema: tabs of tables whose structure changed build
/// their views again, a tab with uncommitted changes keeps its structure (they address columns by position) and says so.
/// </summary>
public sealed class SchemaRefreshTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_changed_table_rebuilds_its_tabs_one_with_uncommitted_changes_keeps_the_old_structure()
    {
        await using var app = new TestApp();
        var scope = await app.OpenAsync(TestApp.Profile("Test"));
        var workspace = app.WorkspaceOf(scope);
        var loaded = await scope.Active.Schema!.GetDetailsAsync(TestApp.Kunden, Ct);
        var plain = new TableTab(workspace.WorkspaceId, TestApp.Kunden) { Details = loaded };
        var notLoaded = new TableTab(workspace.WorkspaceId, TestApp.Kunden);
        workspace.Tabs.AddRange([plain, notLoaded]);
        var pending = app.AddPendingWork(scope);
        pending.Details = loaded;

        var unchanged = await app.Lifecycle.RefreshSchemaAsync(scope);
        Assert.Null(unchanged.Error);
        Assert.Empty(unchanged.Warnings);
        Assert.Equal(0, plain.StructureVersion);

        // ALTER TABLE kunden ADD fax VARCHAR2(30)
        app.KundenDetails = app.KundenDetails with
        {
            Columns = [.. app.KundenDetails.Columns, new ColumnInfo("FAX", "VARCHAR2", 30, false, null, null, true, false, null, 2)],
        };
        var changed = await app.Lifecycle.RefreshSchemaAsync(scope);

        Assert.Equal(1, plain.StructureVersion);
        Assert.Equal(["ID", "FAX"], plain.Details!.Columns.Select(c => c.Name));
        Assert.Equal(0, notLoaded.StructureVersion); // loads the new structure when it is first shown
        Assert.Equal(0, pending.StructureVersion);
        Assert.Same(loaded, pending.Details);
        Assert.Equal(["KUNDEN"], changed.StaleTabs);
        Assert.Equal(
            ["„KUNDEN“ zeigt noch die alte Struktur – der Tab hat nicht committete Änderungen. Erst committen oder verwerfen, dann den Tab neu öffnen."],
            changed.Warnings);
    }

    [Fact]
    public async Task A_table_that_is_gone_closes_its_tabs()
    {
        await using var app = new TestApp();
        var scope = await app.OpenAsync(TestApp.Profile("Test"));
        var workspace = app.WorkspaceOf(scope);
        workspace.Tabs.Add(new TableTab(workspace.WorkspaceId, new TableSummary("APP_USER", "GIBT_ES_NICHT_MEHR", TableKind.Table)));

        await app.Lifecycle.RefreshSchemaAsync(scope);

        Assert.DoesNotContain(workspace.TableTabs, t => t.Table.Name == "GIBT_ES_NICHT_MEHR");
    }

    /// <summary>
    /// The DDL confirmation names other workspaces of the connection with an open write transaction and the tables they
    /// wrote to: DDL on those fails or waits for them without a way to cancel it (ADR 0019).
    /// </summary>
    [Fact]
    public async Task Other_workspaces_with_written_changes_are_named_with_their_tables()
    {
        await using var app = new TestApp();
        var scope = await app.OpenAsync(TestApp.Profile("Test"));
        var first = scope.Workspaces.Active!.Id;
        var second = (await scope.Workspaces.CreateAsync("Zweiter")).Id;
        app.Shell.SyncWorkspaces(scope.Id, scope.Workspaces.Open, first, scope.Active.Schema!);
        Assert.Empty(app.Lifecycle.OtherWriters(first));

        await scope.Workspaces.ExecuteAsync(second, new QuerySpec("UPDATE \"APP_USER\".\"KUNDEN\" SET name = 'x' WHERE id = 1", []), Ct);
        await scope.Workspaces.ExecuteAsync(second, new QuerySpec("insert into auftrag (id) values (1)", []), Ct);

        var other = Assert.Single(app.Lifecycle.OtherWriters(first));
        Assert.Equal("Zweiter", other.Workspace);
        Assert.Equal(["AUFTRAG", "KUNDEN"], other.Tables);
        Assert.DoesNotContain(app.Lifecycle.OtherWriters(second), w => w.Tables.Count > 0); // the first one wrote nothing
    }

    /// <summary>
    /// DDL on a table another session holds (not started by the schema path, or ORA-00054 of DROP): the SQL editor names who
    /// holds it – if FerretSharp may read V$SESSION.
    /// </summary>
    [Fact]
    public async Task A_ddl_that_met_a_locked_table_names_who_holds_it()
    {
        await using var app = new TestApp();
        var scope = await app.OpenAsync(TestApp.Profile("Test"));
        var busy = new DatabaseException("Nicht gestartet …", inner: new TableBusyException("Nicht gestartet …"));
        var dropped = new DatabaseException("Sperre …", "ORA-00054");
        var holder = new LockHolder(42, "APP_USER", "jdoe", "PC-1234", "MeineApp.exe", "FerretSharp", "Workspace 2", null);

        app.LockHolders = [holder];
        var alter = await app.Lifecycle.LockOfAsync(scope, "ALTER TABLE kunden ADD (fax VARCHAR2(30))", busy);
        var drop = await app.Lifecycle.LockOfAsync(scope, "DROP TABLE app_user.kunden", dropped);
        app.LockHolders = null;
        var unknown = await app.Lifecycle.LockOfAsync(scope, "ALTER TABLE kunden ADD (fax VARCHAR2(30))", busy);

        Assert.Equal(TestApp.Kunden.Ref, alter!.Table);
        Assert.Equal([holder], alter.Holders);
        Assert.Equal([holder], drop!.Holders);
        Assert.Null(unknown!.Holders); // no rights: the list says so
        Assert.Null(await app.Lifecycle.LockOfAsync(scope, "ALTER TABLE kunden ADD (x NUMBER)", new DatabaseException("Spalte gibt es schon", "ORA-01430")));
        Assert.Null(await app.Lifecycle.LockOfAsync(scope, "ALTER TABLE gibt_es_nicht ADD (x NUMBER)", busy));
    }

    [Fact]
    public void New_columns_without_property_are_named_up_to_five()
    {
        var one = new SchemaRefreshResult(null, ["KUNDEN.FAX"], []);
        var many = new SchemaRefreshResult(null, ["T.A", "T.B", "T.C", "T.D", "T.E", "T.F"], []);

        Assert.Equal(["1 neue Spalte ohne Property im C#-Modell: KUNDEN.FAX"], one.Warnings);
        Assert.Equal(["6 neue Spalten ohne Property im C#-Modell: T.A, T.B, T.C, T.D, T.E, …"], many.Warnings);
    }
}
