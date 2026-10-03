using FerretSharp.Core.Oracle;
using FerretSharp.Core.Schema;

namespace FerretSharp.Integration.Tests;

/// <summary>Detail views of the structure page: overview, columns, constraints, indexes, dependencies, DDL.</summary>
public sealed class ObjectDetailsTests(OracleContainerFixture oracle) : IAsyncLifetime
{
    private OracleSession? _session;
    private string _owner = "";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private OracleSchemaReader Reader => new(_session!);

    private TableSummary Table(string name, TableKind kind = TableKind.Table) => new(_owner, name, kind);

    public async ValueTask InitializeAsync()
    {
        var (profile, password) = oracle.RequireProfile();
        await ObjectDetailsSchema.EnsureCreatedAsync(oracle.RequireConnectionString(), Ct);
        _owner = profile.EffectiveSchema;
        _session = await OracleSession.OpenAsync(
            OracleConnectionStringFactory.Create(profile, password), new SessionContext("FerretSharp", "Detail tests"), Ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_session is not null)
        {
            await _session.DisposeAsync();
        }
    }

    [Fact]
    public async Task Columns_carry_comment_virtual_flag_and_default_on_null()
    {
        var details = await Reader.GetDetailsAsync(Table("OD_KUNDE"), Ct);
        var byName = details.Columns.ToDictionary(c => c.Name);

        Assert.Equal("Anzeigename", byName["NAME"].Comment);
        Assert.Null(byName["ID"].Comment);
        Assert.True(byName["LAND"].DefaultOnNull);
        Assert.False(byName["NAME"].DefaultOnNull);
        Assert.True(byName["NAME_GROSS"].IsVirtual);
        Assert.Contains("UPPER", byName["NAME_GROSS"].Default);
        Assert.Equal(["ID", "NAME", "LAND", "MENGE", "RABATT", "NAME_GROSS"], details.Columns.Select(c => c.Name));
    }

    [Fact]
    public async Task Object_info_has_status_dates_and_comment()
    {
        var info = await Reader.GetObjectInfoAsync(Table("OD_KUNDE"), Ct);

        Assert.Equal("VALID", info.Status);
        Assert.Equal("Kunden für Detailtests", info.Comment);
        Assert.NotNull(info.Created);
        Assert.NotNull(info.LastDdl);
        Assert.False(info.Partitioned);
        Assert.False(info.Temporary);

        Assert.True((await Reader.GetObjectInfoAsync(Table("OD_V_KAPUTT", TableKind.View), Ct)).IsInvalid);
    }

    [Fact]
    public async Task Table_list_marks_invalid_views()
    {
        var tables = (await Reader.GetTablesAsync(_owner, Ct)).ToDictionary(t => t.Name);

        Assert.True(tables["OD_V_KAPUTT"].IsInvalid);
        Assert.False(tables["OD_V_KUNDE"].IsInvalid);
        Assert.False(tables["OD_KUNDE"].IsInvalid);
    }

    [Fact]
    public async Task Constraints_include_checks_with_condition_and_state()
    {
        var constraints = await Reader.GetConstraintsAsync(new TableRef(_owner, "OD_KUNDE"), Ct);
        var byName = constraints.ToDictionary(c => c.Name);

        var pk = constraints[0];
        Assert.Equal(("PK_OD_KUNDE", ConstraintType.PrimaryKey), (pk.Name, pk.Type));
        Assert.Equal(["ID"], pk.Columns);

        var menge = byName["CK_OD_MENGE"];
        Assert.Equal(ConstraintType.Check, menge.Type);
        Assert.Contains("MENGE", menge.Condition);
        Assert.True(menge.Enabled);
        Assert.False(menge.IsColumnNotNull);
        Assert.False(byName["CK_OD_RABATT"].Enabled);

        // NAME NOT NULL, and DEFAULT ON NULL makes LAND NOT NULL as well.
        Assert.Equal(["LAND", "NAME"], constraints.Where(c => c.IsColumnNotNull).SelectMany(c => c.Columns).Order());
    }

    [Fact]
    public async Task Foreign_key_constraint_names_referenced_table_columns_and_delete_rule()
    {
        var constraints = await Reader.GetConstraintsAsync(new TableRef(_owner, "OD_AUFTRAG"), Ct);

        var fk = Assert.Single(constraints, c => c.Name == "FK_OD_AUFTRAG_KUNDE");
        Assert.Equal(ConstraintType.ForeignKey, fk.Type);
        Assert.Equal(new TableRef(_owner, "OD_KUNDE"), fk.References);
        Assert.Equal(["KUNDE_ID"], fk.Columns);
        Assert.Equal(["ID"], fk.ReferencedColumns);
        Assert.Equal("CASCADE", fk.DeleteRule);
    }

    [Fact]
    public async Task Indexes_show_expressions_and_descending_columns_without_lob_indexes()
    {
        var kunde = (await Reader.GetIndexesAsync(new TableRef(_owner, "OD_KUNDE"), Ct)).ToDictionary(i => i.Name);

        var upper = Assert.Single(kunde["IX_OD_KUNDE_NAME"].Columns);
        Assert.True(upper.IsExpression);
        Assert.Contains("UPPER", upper.Name);

        Assert.Equal(
            [new IndexColumn("LAND", false, true), new IndexColumn("MENGE", false, false)],
            kunde["IX_OD_KUNDE_LAND"].Columns);
        Assert.True(kunde["PK_OD_KUNDE"].Unique);
        Assert.Equal("VALID", kunde["PK_OD_KUNDE"].Status);

        var auftrag = await Reader.GetIndexesAsync(new TableRef(_owner, "OD_AUFTRAG"), Ct);
        Assert.DoesNotContain(auftrag, i => i.IndexType == "LOB");

        var foreignKeys = (await Reader.GetForeignKeysAsync(_owner, Ct)).Where(f => f.From.Name == "OD_AUFTRAG");
        Assert.Equal(["FK_OD_AUFTRAG_VERK"], IndexAdvice.UnindexedForeignKeys(foreignKeys, auftrag).Select(f => f.Name));
    }

    [Fact]
    public async Task Dependencies_go_both_ways_with_status()
    {
        var view = await Reader.GetDependenciesAsync(Table("OD_V_KUNDE", TableKind.View), Ct);
        Assert.Contains(view.Uses, d => d.Name == "OD_KUNDE" && d.Type == "TABLE" && d.Owner == _owner);

        var table = await Reader.GetDependenciesAsync(Table("OD_KUNDE"), Ct);
        Assert.Contains(table.UsedBy, d => d.Name == "OD_V_KUNDE" && d.Type == "VIEW" && !d.IsInvalid);

        var temp = await Reader.GetDependenciesAsync(Table("OD_TEMP"), Ct);
        Assert.Contains(temp.UsedBy, d => d.Name == "OD_V_KAPUTT" && d.IsInvalid);
    }

    [Fact]
    public async Task Ddl_comes_from_dbms_metadata()
    {
        var table = await Reader.GetDdlAsync(Table("OD_KUNDE"), Ct);
        Assert.StartsWith("CREATE TABLE", table);
        Assert.Contains("\"OD_KUNDE\"", table);

        var view = await Reader.GetDdlAsync(Table("OD_V_KUNDE", TableKind.View), Ct);
        Assert.Contains("VIEW", view);
        Assert.Contains("\"OD_V_KUNDE\"", view);
    }
}
