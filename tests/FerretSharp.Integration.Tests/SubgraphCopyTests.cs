using FerretSharp.Core.Connections;
using FerretSharp.Core.Data;
using FerretSharp.Core.Oracle;
using FerretSharp.Core.Query;
using FerretSharp.Core.Relations;
using FerretSharp.Core.Schema;

namespace FerretSharp.Integration.Tests;

/// <summary>
/// Copying a subgraph (WP-42) from the app user's schema into FERRET_COPY, as between two connections: collected on the
/// source's read path, checked against the target, written as one action into the target session's transaction. Each test
/// has its own sessions; disposing the target rolls back what a test wrote.
/// </summary>
public sealed class SubgraphCopyTests(OracleContainerFixture oracle) : IAsyncLifetime
{
    private OracleSession? _source;
    private OracleSession? _target;
    private string _sourceOwner = "";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        var connectionString = oracle.RequireConnectionString();
        await CopySchema.EnsureCreatedAsync(connectionString, Ct);
        Assert.SkipUnless(CopySchema.Available, CopySchema.UnavailableReason ?? "Copy schema not created.");

        var (profile, password) = oracle.RequireProfile();
        _sourceOwner = profile.EffectiveSchema;
        _source = await OracleSession.OpenAsync(OracleConnectionStringFactory.Create(profile, password), new SessionContext("FerretSharp", "Copy source"), Ct);
        _target = await OracleSession.OpenAsync(
            OracleConnectionStringFactory.Create(profile with { User = CopySchema.TargetOwner }, CopySchema.TargetPassword),
            new SessionContext("FerretSharp", "Copy target"), Ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_source is not null)
        {
            await _source.DisposeAsync();
        }

        if (_target is not null)
        {
            await _target.DisposeAsync();
        }
    }

    private async Task<SchemaCache> SchemaAsync(OracleSession session, string owner)
    {
        var schema = new SchemaCache(new OracleSchemaReader(session), owner);
        await schema.LoadAsync(Ct);
        return schema;
    }

    private static async Task<TableDetails> DetailsAsync(SchemaCache schema, string table) =>
        await schema.GetDetailsAsync(schema.Find(new TableRef(schema.Owner, table))!, Ct);

    /// <summary>Customer 1 with its orders, their positions and deliveries, and all parents – checked against FERRET_COPY.</summary>
    private async Task<(SubgraphCopyPlan Plan, RowGraph Graph)> PlanCustomerAsync()
    {
        var source = await SchemaAsync(_source!, _sourceOwner);
        var relations = RelationGraph.Of(source);
        ForeignKeyInfo Named(string name) => relations.Relations.Single(r => r.Name == name);
        var traversal = new RowTraversal(new OracleDataAccess(_source!), relations,
            async (table, ct) => source.Find(table) is { } summary ? await source.GetDetailsAsync(summary, ct) : null, wholeLobs: true);
        var paths = new TraversalPlan([[Named("FK_CP_AUFTRAG_KUNDE"), Named("FK_CP_POS_AUFTRAG"), Named("FK_CP_LIEF_POS")]], new HashSet<string>());

        var graph = await traversal.CollectAsync(await DetailsAsync(source, "CP_KUNDE"), [new RowKey.PrimaryKey([1m])], paths, null, Ct);
        var target = await SchemaAsync(_target!, CopySchema.TargetOwner);
        var plan = await new SubgraphCopyPlanner(_sourceOwner, target, new OracleDataAccess(_target!)).PlanAsync(graph, Ct);
        return (plan, graph);
    }

    private async Task<T?> ScalarAsync<T>(string sql) =>
        await _target!.ExecuteReaderAsync(sql, [], async (reader, ct) => await reader.ReadAsync(ct) && !reader.IsDBNull(0) ? (T)reader.GetValue(0) : default, Ct);

    private async Task<int> CountAsync(string table) => (int)(await ScalarAsync<decimal>($"SELECT COUNT(*) FROM {table}"));

    [Fact]
    public async Task A_customer_with_orders_positions_deliveries_and_the_managers_up_the_chain_lands_in_the_target_as_one_action()
    {
        var (plan, graph) = await PlanCustomerAsync();

        Assert.Equal(11, graph.RowCount);
        Assert.Empty(graph.Problems);
        Assert.Equal(["CP_KUNDE", "CP_MITARBEITER", "CP_AUFTRAG", "CP_POSITION", "CP_LIEFERUNG"], plan.Tables.Select(t => t.Table.Name));
        Assert.Equal([10m, 11m, 12m], plan.Tables[1].Rows.Select(r => r.Values[0])); // the manager before the staff
        Assert.Equal([new RowKey.PrimaryKey([1m])], plan.Tables[0].Existing);
        var hint = Assert.Single(plan.Issues);
        Assert.Equal((CopySeverity.Hint, "Trigger auf CP_AUFTRAG im Ziel laufen beim Kopieren mit: TRG_CP_AUFTRAG_LOG."), (hint.Severity, hint.Message));

        var editor = new OracleDataEditor(_target!);
        var modes = new Dictionary<TableRef, ConflictMode> { [plan.Tables[0].Table] = ConflictMode.Update };
        var action = await editor.CopyAsync(plan.ToWrite(modes, "SOURCE", 3), Ct);

        Assert.Equal(WriteActionKind.Copy, action.Kind);
        Assert.Equal(11, action.Rows);
        Assert.Equal("Kopie aus SOURCE: 11 Zeilen", action.Description);
        Assert.Equal([action], editor.Actions);
        Assert.Equal("Peters Pizza", await ScalarAsync<string>("SELECT NAME FROM CP_KUNDE WHERE ID = 1"));
        Assert.Equal(8000m, await ScalarAsync<decimal>("SELECT DBMS_LOB.GETLENGTH(NOTIZ) FROM CP_KUNDE WHERE ID = 1"));
        Assert.Equal("CAFE01", await ScalarAsync<string>("SELECT RAWTOHEX(DBMS_LOB.SUBSTR(BILD, 10, 1)) FROM CP_KUNDE WHERE ID = 1"));
        Assert.Equal(3, await CountAsync("CP_MITARBEITER"));
        Assert.Equal("1234.5|AB |2026-10-01 12:34:56.123456", await ScalarAsync<string>(
            "SELECT TO_CHAR(BETRAG, 'TM9', 'NLS_NUMERIC_CHARACTERS=.,') || '|' || KZ || '|' || TO_CHAR(TS, 'YYYY-MM-DD HH24:MI:SS.FF6') FROM CP_AUFTRAG WHERE ID = 100"));
        Assert.Equal("Käse – €", await ScalarAsync<string>("SELECT ARTIKEL FROM CP_POSITION WHERE AUFTRAG_ID = 100 AND POS_NR = 1"));
        Assert.Equal(2, await CountAsync("CP_LIEFERUNG"));
        Assert.Equal(2, await CountAsync("CP_AUFTRAG_LOG")); // the trigger fired

        await editor.UndoLastAsync(action.Id, Ct);
        Assert.Equal(0, await CountAsync("CP_AUFTRAG"));
        Assert.Equal("Peters Pizza (Test)", await ScalarAsync<string>("SELECT NAME FROM CP_KUNDE WHERE ID = 1"));

        var redone = await editor.RedoAsync(action.Id, Ct);
        Assert.Equal(action.Id, redone.RedoOf);
        Assert.Equal(2, await CountAsync("CP_AUFTRAG"));
        Assert.Equal(1, await CountAsync("CP_KUNDE"));
    }

    [Fact]
    public async Task A_copy_that_fails_keeps_nothing_and_names_the_table()
    {
        var target = await SchemaAsync(_target!, CopySchema.TargetOwner);
        var kunde = await DetailsAsync(target, "CP_KUNDE");
        var auftrag = await DetailsAsync(target, "CP_AUFTRAG");
        var editor = new OracleDataEditor(_target!);
        var copy = new CopyWrite("Kopie", [
            new CopyInsert(kunde, DmlBuilder.InsertBatch(kunde, [0, 1], [[3m, "Neu"]])),
            new CopyInsert(auftrag, DmlBuilder.InsertBatch(auftrag, [0, 1], [[300m, 3m], [301m, 999m]])),
        ]);

        var ex = await Assert.ThrowsAsync<CopyFailedException>(() => editor.CopyAsync(copy, Ct));

        Assert.StartsWith("Kopieren in CP_AUFTRAG gescheitert, von der Kopie bleibt nichts:", ex.Message);
        Assert.Contains(FerretSharp.Core.Resources.DataText.WriteFailedParentMissing, ex.Message);
        Assert.Contains("FK_CP_AUFTRAG_KUNDE", ex.Message);
        Assert.Same(copy.Steps[1], ex.Step);
        Assert.Empty(editor.Actions);
        Assert.Equal(1, await CountAsync("CP_KUNDE"));
    }

    [Fact]
    public async Task An_update_of_a_row_deleted_meanwhile_fails_the_copy()
    {
        var target = await SchemaAsync(_target!, CopySchema.TargetOwner);
        var kunde = await DetailsAsync(target, "CP_KUNDE");
        var editor = new OracleDataEditor(_target!);
        var copy = new CopyWrite("Kopie", [new CopyUpdate(kunde, new RowKey.PrimaryKey([77m]), new Dictionary<int, object?> { [1] = "x" })]);

        var ex = await Assert.ThrowsAsync<CopyFailedException>(() => editor.CopyAsync(copy, Ct));

        Assert.Contains("CP_KUNDE", ex.Message);
        Assert.Empty(editor.Actions);
    }

    [Fact]
    public async Task A_locked_workspace_takes_no_copy()
    {
        var target = await SchemaAsync(_target!, CopySchema.TargetOwner);
        var kunde = await DetailsAsync(target, "CP_KUNDE");
        await _target!.UseReadOnlySnapshotsAsync(Ct);

        await Assert.ThrowsAsync<RefusedException>(() => new OracleDataEditor(_target).CopyAsync(
            new CopyWrite("Kopie", [new CopyInsert(kunde, DmlBuilder.InsertBatch(kunde, [0, 1], [[4m, "x"]]))]), Ct));
    }

    [Fact]
    public async Task The_dictionary_tells_identity_always_and_the_enabled_triggers_of_a_table()
    {
        var target = await SchemaAsync(_target!, CopySchema.TargetOwner);
        var always = await DetailsAsync(target, "CP_IDENTITY");
        var byDefault = await DetailsAsync(target, "CP_IDENTITY_DEF");

        Assert.Equal([(true, true), (false, false)], always.Columns.Select(c => (c.IsIdentity, c.IdentityAlways)));
        Assert.Equal([(true, false), (false, false)], byDefault.Columns.Select(c => (c.IsIdentity, c.IdentityAlways)));
        Assert.Equal([new TableRef(CopySchema.TargetOwner, "TRG_CP_AUFTRAG_LOG")], await target.GetTableTriggersAsync(new TableRef(CopySchema.TargetOwner, "CP_AUFTRAG"), Ct));
        Assert.Empty(await target.GetTableTriggersAsync(new TableRef(CopySchema.TargetOwner, "CP_KUNDE"), Ct));
    }

    [Fact]
    public async Task Rows_are_read_by_composite_key_and_by_rowid()
    {
        var source = await SchemaAsync(_source!, _sourceOwner);
        var position = await DetailsAsync(source, "CP_POSITION");
        var data = new OracleDataAccess(_source!);

        var byKey = await data.ReadByKeysAsync(position, [0, 1], [[100m, 2m], [101m, 1m], [999m, 1m]], wholeLobs: false, Ct);
        var byRowId = await data.ReadByKeysAsync(position, null, [[((RowKey.RowId)new RowKey.RowId(await RowIdAsync())).Value]], wholeLobs: false, Ct);

        Assert.Equal([new RowKey.PrimaryKey([100m, 2m]), new RowKey.PrimaryKey([101m, 1m])], byKey.Select(r => r.Key));
        Assert.Equal("Brot", Assert.Single(byRowId).Values[2]);
    }

    private async Task<string> RowIdAsync() =>
        await _source!.ExecuteReaderAsync("SELECT ROWIDTOCHAR(ROWID) FROM CP_POSITION WHERE AUFTRAG_ID = 100 AND POS_NR = 2", [],
            async (reader, ct) => await reader.ReadAsync(ct) ? reader.GetString(0) : "", Ct);
}
