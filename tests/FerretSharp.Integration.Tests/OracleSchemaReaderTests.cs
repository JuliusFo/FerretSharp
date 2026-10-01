using FerretSharp.Core.Oracle;
using FerretSharp.Core.Schema;

namespace FerretSharp.Integration.Tests;

public class OracleSchemaReaderTests(OracleContainerFixture oracle) : IAsyncLifetime
{
    private OracleSession? _session;
    private string _owner = "";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private OracleSchemaReader Reader => new(_session!);

    public async ValueTask InitializeAsync()
    {
        var (profile, password) = oracle.RequireProfile();
        await SampleSchema.EnsureCreatedAsync(oracle.RequireConnectionString(), Ct);
        _owner = profile.EffectiveSchema;
        _session = await OracleSession.OpenAsync(
            OracleConnectionStringFactory.Create(profile, password), new SessionContext("FerretSharp", "Schema tests"), Ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_session is not null)
        {
            await _session.DisposeAsync();
        }
    }

    [Fact]
    public async Task Lists_tables_views_and_mviews_without_internal_objects()
    {
        var tables = await Reader.GetTablesAsync(_owner, Ct);
        var byName = tables.ToDictionary(t => t.Name, t => t.Kind);

        Assert.Equal(TableKind.Table, byName["KUNDEN"]);
        Assert.Equal(TableKind.Table, byName["MixedCase"]);
        Assert.Equal(TableKind.Table, byName["LAND_IOT"]);
        Assert.Equal(TableKind.View, byName["V_KUNDEN_AUFTRAEGE"]);

        Assert.DoesNotContain(tables, t => t.Name.StartsWith("BIN$", StringComparison.Ordinal));
        Assert.DoesNotContain(tables, t => t.Name.StartsWith("SYS_IOT_OVER", StringComparison.Ordinal));
        Assert.DoesNotContain(tables, t => t.Name == "WEG_DAMIT");
        Assert.Equal(tables.Select(t => t.Name).Order(StringComparer.Ordinal), tables.Select(t => t.Name));
    }

    [Fact]
    public async Task Lists_materialized_view_once_and_not_its_container_table()
    {
        Assert.SkipUnless(SampleSchema.HasMaterializedView, "Test user may not create materialized views.");

        var tables = await Reader.GetTablesAsync(_owner, Ct);

        var mview = Assert.Single(tables, t => t.Name == "MV_UMSATZ");
        Assert.Equal(TableKind.MaterializedView, mview.Kind);
    }

    [Fact]
    public async Task Reads_simple_and_composite_foreign_keys()
    {
        var fks = (await Reader.GetForeignKeysAsync(_owner, Ct)).ToDictionary(f => f.Name);

        var simple = fks["FK_AUFTRAG_KUNDE"];
        Assert.Equal(new TableRef(_owner, "AUFTRAG"), simple.From);
        Assert.Equal(new TableRef(_owner, "KUNDEN"), simple.To);
        Assert.Equal(["KUNDE_ID"], simple.FromColumns);
        Assert.Equal(["KUNDE_ID"], simple.ToColumns);
        Assert.Equal(FkSource.Declared, simple.Source);

        var composite = fks["FK_LIEF_POS"];
        Assert.Equal(["AUFTRAG_ID", "POS_NR"], composite.FromColumns);
        Assert.Equal(["AUFTRAG_ID", "POS_NR"], composite.ToColumns);
    }

    [Fact]
    public async Task Reads_columns_types_defaults_and_keys()
    {
        var details = await Reader.GetDetailsAsync(new TableSummary(_owner, "KUNDEN", TableKind.Table), Ct);
        var columns = details.Columns.ToDictionary(c => c.Name);

        Assert.Equal(["KUNDE_ID", "NAME", "KUERZEL", "ERSTELLT_AM", "UMSATZ", "ANZAHL"], details.Columns.Select(c => c.Name));
        Assert.Equal("NUMBER(10)", columns["KUNDE_ID"].DisplayType);
        Assert.Equal("VARCHAR2(100 CHAR)", columns["NAME"].DisplayType);
        Assert.Equal("CHAR(3)", columns["KUERZEL"].DisplayType);
        Assert.Equal("DATE", columns["ERSTELLT_AM"].DisplayType);
        Assert.Equal("NUMBER(12,2)", columns["UMSATZ"].DisplayType);
        Assert.Equal("INTEGER", columns["ANZAHL"].DisplayType);
        Assert.Equal("SYSDATE", columns["ERSTELLT_AM"].Default);
        Assert.False(columns["NAME"].Nullable);
        Assert.True(columns["UMSATZ"].Nullable);

        Assert.Equal(["KUNDE_ID"], details.PrimaryKey);
        Assert.Equal(["KUERZEL"], Assert.Single(details.UniqueKeys));
        Assert.False(details.IsIndexOrganized);
    }

    [Fact]
    public async Task Detects_identity_composite_primary_key_and_iot()
    {
        var auftrag = await Reader.GetDetailsAsync(new TableSummary(_owner, "AUFTRAG", TableKind.Table), Ct);
        Assert.True(auftrag.Columns.Single(c => c.Name == "AUFTRAG_ID").IsIdentity);
        Assert.Equal("CLOB", auftrag.Columns.Single(c => c.Name == "NOTIZ").DisplayType);

        var position = await Reader.GetDetailsAsync(new TableSummary(_owner, "AUFTRAG_POSITION", TableKind.Table), Ct);
        Assert.Equal(["AUFTRAG_ID", "POS_NR"], position.PrimaryKey);

        var iot = await Reader.GetDetailsAsync(new TableSummary(_owner, "LAND_IOT", TableKind.Table), Ct);
        Assert.True(iot.IsIndexOrganized);
    }

    [Fact]
    public async Task Handles_quoted_mixed_case_identifiers()
    {
        var details = await Reader.GetDetailsAsync(new TableSummary(_owner, "MixedCase", TableKind.Table), Ct);

        Assert.Equal(["Id", "Wert", "raw col"], details.Columns.Select(c => c.Name));
        Assert.Equal("NVARCHAR2(20)", details.Columns[1].DisplayType);
        Assert.Equal("RAW(16)", details.Columns[2].DisplayType);
        Assert.Equal(["Id"], details.PrimaryKey);
    }

    [Fact]
    public async Task View_has_columns_but_no_primary_key()
    {
        var details = await Reader.GetDetailsAsync(new TableSummary(_owner, "V_KUNDEN_AUFTRAEGE", TableKind.View), Ct);

        Assert.Equal(["KUNDE_ID", "NAME", "ANZAHL"], details.Columns.Select(c => c.Name));
        Assert.Empty(details.PrimaryKey);
        Assert.False(details.IsIndexOrganized);
        Assert.StartsWith("SELECT k.KUNDE_ID", details.Definition);
        Assert.Contains("GROUP BY k.KUNDE_ID, k.NAME", details.Definition);
        Assert.False(details.DefinitionTruncated);
    }

    [Fact]
    public async Task Tables_have_no_definition_and_mviews_show_their_query()
    {
        var table = await Reader.GetDetailsAsync(new TableSummary(_owner, "KUNDEN", TableKind.Table), Ct);
        Assert.Null(table.Definition);

        Assert.SkipUnless(SampleSchema.HasMaterializedView, "Test user may not create materialized views.");
        var mview = await Reader.GetDetailsAsync(new TableSummary(_owner, "MV_UMSATZ", TableKind.MaterializedView), Ct);
        Assert.Contains("SUM(UMSATZ)", mview.Definition);
    }

    [Fact]
    public async Task Schema_cache_answers_incoming_and_outgoing_relationships()
    {
        var cache = new SchemaCache(Reader, _owner);
        await cache.LoadAsync(Ct);
        var kunden = new TableRef(_owner, "KUNDEN");
        var auftrag = new TableRef(_owner, "AUFTRAG");

        Assert.Contains(cache.IncomingOf(kunden), f => f.From == auftrag);
        Assert.Contains(cache.OutgoingOf(auftrag), f => f.To == kunden);
        Assert.Empty(cache.OutgoingOf(kunden));
    }
}
