using FerretSharp.Core.Data;
using FerretSharp.Core.Query;
using FerretSharp.Core.Schema;
using NSubstitute;

namespace FerretSharp.Core.Tests.Query;

/// <summary>
/// WP-48: result columns traced back to table columns from what ODP.NET reports (as measured in
/// <c>SqlResultOriginDriverTests</c>) – and FK jumps from result rows.
/// </summary>
public sealed class SqlResultOriginsTests
{
    private const string Owner = "APP";

    private static ColumnInfo Number(string name, int position) => new(name, "NUMBER", null, false, 10, 0, false, false, null, position);

    private static ColumnInfo Text(string name, int position) => new(name, "VARCHAR2", 50, true, null, null, true, false, null, position);

    private static readonly TableDetails Kunden = new(new TableSummary(Owner, "KUNDEN", TableKind.Table),
        [Number("KUNDE_ID", 1), Text("NAME", 2)], ["KUNDE_ID"], [], false);

    private static readonly TableDetails Auftrag = new(new TableSummary(Owner, "AUFTRAG", TableKind.Table),
        [Number("AUFTRAG_ID", 1), Number("KUNDE_ID", 2)], ["AUFTRAG_ID"], [], false);

    private static readonly TableDetails Mitarbeiter = new(new TableSummary(Owner, "MITARBEITER", TableKind.Table),
        [Number("ID", 1), Number("CHEF_ID", 2)], ["ID"], [], false);

    private static readonly TableDetails Lieferung = new(new TableSummary(Owner, "LIEFERUNG", TableKind.Table),
        [Number("LIEFERUNG_ID", 1), Number("AUFTRAG_ID", 2), Number("POS_NR", 3)], ["LIEFERUNG_ID"], [], false);

    /// <summary>A table named like an inline view's alias.</summary>
    private static readonly TableDetails X = new(new TableSummary(Owner, "X", TableKind.Table), [Number("N", 1)], [], [], false);

    private static readonly TableDetails Laender = new(
        new TableSummary("ANDERE", "LAENDER", TableKind.Table, new SynonymInfo(SynonymInfo.PublicOwner, "LAND")), [Text("CODE", 1)], ["CODE"], [], false);

    private static readonly ForeignKeyInfo AuftragKunde = new("FK_AUFTRAG_KUNDE", Auftrag.Table.Ref, ["KUNDE_ID"], Kunden.Table.Ref, ["KUNDE_ID"], FkSource.Declared);
    private static readonly ForeignKeyInfo Chef = new("FK_CHEF", Mitarbeiter.Table.Ref, ["CHEF_ID"], Mitarbeiter.Table.Ref, ["ID"], FkSource.ClrModel);

    private static readonly ForeignKeyInfo LieferungPosition =
        new("FK_LIEF_POS", Lieferung.Table.Ref, ["AUFTRAG_ID", "POS_NR"], new TableRef(Owner, "AUFTRAG_POSITION"), ["AUFTRAG_ID", "POS_NR"], FkSource.Declared);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <param name="synonymTargets">Objects reached through synonyms; <see cref="Laender"/> if none are given.</param>
    private static async Task<SchemaCache> SchemaAsync(params TableDetails[] synonymTargets)
    {
        TableDetails[] own = [Kunden, Auftrag, Mitarbeiter, Lieferung, X];
        TableDetails[] others = synonymTargets.Length > 0 ? synonymTargets : [Laender];
        var reader = Substitute.For<ISchemaReader>();
        reader.GetTablesAsync(Owner, Arg.Any<CancellationToken>()).Returns(own.Select(t => t.Table).ToList());
        reader.GetSynonymTargetsAsync(Owner, Arg.Any<CancellationToken>()).Returns(new SynonymTargets([.. others.Select(t => t.Table)], []));
        reader.GetForeignKeysAsync(Owner, Arg.Any<CancellationToken>()).Returns((IReadOnlyList<ForeignKeyInfo>)[AuftragKunde, LieferungPosition]);
        reader.GetForeignKeysAsync(Arg.Is<string>(o => o != Owner), Arg.Any<CancellationToken>()).Returns((IReadOnlyList<ForeignKeyInfo>)[]);
        foreach (var table in own.Concat(others))
        {
            reader.GetDetailsAsync(Arg.Is<TableSummary>(t => t.Ref == table.Table.Ref), Arg.Any<CancellationToken>()).Returns(table);
        }

        var schema = new SchemaCache(reader, Owner);
        await schema.LoadAsync(Ct);
        schema.SetForeignKeys(FkSource.ClrModel, [Chef]);
        return schema;
    }

    /// <summary>Result columns with the origin the driver reports: <c>"TABLE.COLUMN"</c>, <c>"OWNER.TABLE.COLUMN"</c>, or null.</summary>
    private static List<SqlColumn> Columns(params string?[] origins) => origins.Select((o, i) =>
    {
        var parts = o?.Split('.');
        var origin = parts switch
        {
            [var table, var column] => new SqlColumnOrigin(null, table, column),
            [var owner, var table, var column] => new SqlColumnOrigin(owner, table, column),
            _ => null,
        };
        return new SqlColumn($"C{i}", Number(parts?[^1] ?? "X", i + 1), origin);
    }).ToList();

    private static async Task<string?[]> ResolveAsync(string sql, params string?[] origins) =>
        [.. SqlResultOrigins.Resolve(sql, Columns(origins), await SchemaAsync()).Select(o => o is null ? null : $"{o.Source}:{o.Table.Ref}.{o.Column}")];

    [Fact]
    public async Task A_table_as_written_is_the_origin()
    {
        Assert.Equal(["KUNDEN:APP.KUNDEN.KUNDE_ID", "KUNDEN:APP.KUNDEN.NAME", null],
            await ResolveAsync("SELECT KUNDE_ID, NAME AS N, 1 + 1 FROM kunden", "KUNDEN.KUNDE_ID", "KUNDEN.NAME", null));
    }

    [Fact]
    public async Task An_unquoted_alias_comes_resolved_from_the_driver()
    {
        Assert.Equal(["AUFTRAG:APP.AUFTRAG.AUFTRAG_ID", "KUNDEN:APP.KUNDEN.NAME"],
            await ResolveAsync("SELECT a.AUFTRAG_ID, k.NAME FROM AUFTRAG a JOIN KUNDEN k ON k.KUNDE_ID = a.KUNDE_ID", "AUFTRAG.AUFTRAG_ID", "KUNDEN.NAME"));
    }

    [Fact]
    public async Task A_quoted_alias_of_EF_Core_stands_for_its_table()
    {
        const string sql = """
            SELECT "a"."AUFTRAG_ID", "a"."KUNDE_ID", "k"."KUNDE_ID", "k"."NAME"
            FROM "AUFTRAG" "a" INNER JOIN "KUNDEN" "k" ON "a"."KUNDE_ID" = "k"."KUNDE_ID"
            """;
        Assert.Equal(["\"a\":APP.AUFTRAG.AUFTRAG_ID", "\"a\":APP.AUFTRAG.KUNDE_ID", "\"k\":APP.KUNDEN.KUNDE_ID", "\"k\":APP.KUNDEN.NAME"],
            await ResolveAsync(sql, "a.AUFTRAG_ID", "a.KUNDE_ID", "k.KUNDE_ID", "k.NAME"));
    }

    [Fact]
    public async Task An_alias_after_CROSS_APPLY_stands_for_its_table_but_the_applied_subquery_has_no_origin()
    {
        const string sql = "SELECT k.KUNDE_ID, x.AUFTRAG_ID FROM KUNDEN k CROSS APPLY (SELECT a.AUFTRAG_ID FROM AUFTRAG a WHERE a.KUNDE_ID = k.KUNDE_ID) x";
        Assert.Equal(["\"K\":APP.KUNDEN.KUNDE_ID", null], await ResolveAsync(sql, "K.KUNDE_ID", "X.AUFTRAG_ID"));
    }

    [Fact]
    public async Task An_inline_view_has_no_origin_even_if_a_table_has_its_alias_as_name()
    {
        Assert.Equal([null], await ResolveAsync("SELECT x.N FROM (SELECT NAME AS N FROM KUNDEN) x", "X.N"));
        Assert.Equal([null], await ResolveAsync("""SELECT "t"."KUNDE_ID" FROM (SELECT "k"."KUNDE_ID" FROM "KUNDEN" "k" FETCH FIRST 5 ROWS ONLY) "t" """, "t.KUNDE_ID"));
    }

    [Fact]
    public async Task The_owner_comes_from_the_statement()
    {
        Assert.Equal(["APP.KUNDEN:APP.KUNDEN.KUNDE_ID"], await ResolveAsync("SELECT k.KUNDE_ID FROM APP.KUNDEN k", "APP.KUNDEN.KUNDE_ID"));
        Assert.Equal([null], await ResolveAsync("SELECT k.KUNDE_ID FROM FREMD.KUNDEN k", "FREMD.KUNDEN.KUNDE_ID"));
    }

    [Fact]
    public async Task A_synonym_stands_for_its_target()
    {
        Assert.Equal(["LAND:ANDERE.LAENDER.CODE"], await ResolveAsync("SELECT CODE FROM LAND", "LAND.CODE"));
    }

    [Theory]
    [InlineData("WITH k AS (SELECT KUNDE_ID FROM KUNDEN) SELECT KUNDE_ID FROM KUNDEN")]
    [InlineData("SELECT KUNDE_ID FROM KUNDEN UNION ALL SELECT KUNDE_ID FROM AUFTRAG")]
    [InlineData("SELECT KUNDE_ID FROM KUNDEN MINUS SELECT KUNDE_ID FROM AUFTRAG")]
    [InlineData("SELECT KUNDE_ID FROM UNBEKANNT")]
    [InlineData("SELECT KUNDE_ID FROM AUFTRAG")]
    public async Task Set_operators_WITH_and_tables_the_statement_does_not_name_have_no_origin(string sql)
    {
        Assert.Equal([null], await ResolveAsync(sql, "KUNDEN.KUNDE_ID"));
    }

    [Fact]
    public async Task A_set_operator_inside_a_subquery_leaves_the_outer_columns()
    {
        Assert.Equal(["KUNDEN:APP.KUNDEN.KUNDE_ID"],
            await ResolveAsync("SELECT KUNDE_ID FROM KUNDEN WHERE KUNDE_ID IN (SELECT KUNDE_ID FROM AUFTRAG UNION SELECT 1 FROM DUAL)", "KUNDEN.KUNDE_ID"));
    }

    [Fact]
    public async Task A_self_join_with_unquoted_aliases_cannot_be_told_apart()
    {
        Assert.Equal([null, null],
            await ResolveAsync("SELECT m.ID, c.ID FROM MITARBEITER m JOIN MITARBEITER c ON c.ID = m.CHEF_ID", "MITARBEITER.ID", "MITARBEITER.ID"));
    }

    [Fact]
    public async Task A_self_join_with_quoted_aliases_has_two_sources()
    {
        Assert.Equal(["\"m\":APP.MITARBEITER.CHEF_ID", "\"c\":APP.MITARBEITER.ID"],
            await ResolveAsync("""SELECT "m"."CHEF_ID", "c"."ID" FROM "MITARBEITER" "m" JOIN "MITARBEITER" "c" ON "c"."ID" = "m"."CHEF_ID" """, "m.CHEF_ID", "c.ID"));
    }

    [Fact]
    public async Task The_same_table_in_a_subquery_does_not_make_the_outer_one_unclear()
    {
        Assert.Equal(["KUNDEN:APP.KUNDEN.KUNDE_ID"],
            await ResolveAsync("SELECT k.KUNDE_ID FROM KUNDEN k WHERE EXISTS (SELECT 1 FROM KUNDEN k2 WHERE k2.KUNDE_ID = k.KUNDE_ID)", "KUNDEN.KUNDE_ID"));
    }

    [Fact]
    public async Task A_name_that_is_an_alias_and_a_table_is_unclear()
    {
        Assert.Equal([null], await ResolveAsync("SELECT KUNDEN.AUFTRAG_ID FROM AUFTRAG KUNDEN JOIN KUNDEN k ON 1 = 1", "KUNDEN.AUFTRAG_ID"));
    }

    [Fact]
    public async Task Sources_group_the_columns_of_one_row_and_skip_columns_the_table_does_not_have()
    {
        const string sql = """SELECT "a"."AUFTRAG_ID", "a"."KUNDE_ID", "k"."NAME", "a".ROWID FROM "AUFTRAG" "a" JOIN "KUNDEN" "k" ON 1 = 1""";
        var sources = await SqlResultSources.LoadAsync(sql, Columns("a.AUFTRAG_ID", "a.KUNDE_ID", "k.NAME", "a.ROWID"), await SchemaAsync(), Ct);

        Assert.Equal(["AUFTRAG", "KUNDEN"], sources.Sources.Select(s => s.Table.Name));
        Assert.Equal([(0, 0), (1, 1)], sources.Sources[0].Columns);
        Assert.Equal([(2, 1)], sources.Sources[1].Columns);
        Assert.Equal(["AUFTRAG_ID", "KUNDE_ID"], sources.Sources[0].Projection.Columns.Select(c => c.Name));
        Assert.All(sources.Sources, s => Assert.Null(s.TableColumnOf(3)));
        Assert.Equal(1, sources.Sources[1].TableColumnOf(2));
    }

    private static RowData Row(params object?[] values) => new(RowKey.None.Instance, values);

    [Fact]
    public async Task Result_rows_jump_along_foreign_keys_of_each_source()
    {
        const string sql = """SELECT "a"."AUFTRAG_ID", "a"."KUNDE_ID", "k"."KUNDE_ID", "k"."NAME" FROM "AUFTRAG" "a" JOIN "KUNDEN" "k" ON 1 = 1""";
        var schema = await SchemaAsync();
        var sources = await SqlResultSources.LoadAsync(sql, Columns("a.AUFTRAG_ID", "a.KUNDE_ID", "k.KUNDE_ID", "k.NAME"), schema, Ct);

        var jumps = sources.JumpsFor([Row(10m, 4711m, 4711m, "Huber"), Row(11m, 4712m, 4712m, "Maier")], schema.OutgoingOf, schema.IncomingOf);

        var outgoing = Assert.Single(jumps, j => j.Jump.Direction == JumpDirection.Outgoing);
        Assert.Equal("KUNDEN", outgoing.Jump.Table.Name);
        Assert.Equal(FilterOperator.In, Assert.Single(outgoing.Jump.Filters).Op);
        Assert.Equal(["4711", "4712"], outgoing.Jump.Filters[0].Values);
        Assert.True(outgoing.Involves(1));
        Assert.False(outgoing.Involves(2)); // the same column name, but of the other source

        var incoming = Assert.Single(jumps, j => j.Jump.Direction == JumpDirection.Incoming);
        Assert.Equal("AUFTRAG", incoming.Jump.Table.Name);
        Assert.Equal("KUNDE_ID", incoming.Jump.Filters[0].Column);
        Assert.True(incoming.Involves(2));
        Assert.Equal(JumpDirection.Outgoing, jumps[0].Jump.Direction);
    }

    [Fact]
    public async Task Without_the_key_in_the_result_there_is_no_incoming_jump()
    {
        var schema = await SchemaAsync();
        var sources = await SqlResultSources.LoadAsync("SELECT NAME FROM KUNDEN", Columns("KUNDEN.NAME"), schema, Ct);

        Assert.Empty(sources.JumpsFor([Row("Huber")], schema.OutgoingOf, schema.IncomingOf));
    }

    [Fact]
    public async Task A_composite_foreign_key_needs_all_its_columns_in_the_result()
    {
        var schema = await SchemaAsync();
        var partial = await SqlResultSources.LoadAsync("SELECT LIEFERUNG_ID, AUFTRAG_ID FROM LIEFERUNG", Columns("LIEFERUNG.LIEFERUNG_ID", "LIEFERUNG.AUFTRAG_ID"), schema, Ct);
        var whole = await SqlResultSources.LoadAsync("SELECT AUFTRAG_ID, POS_NR FROM LIEFERUNG", Columns("LIEFERUNG.AUFTRAG_ID", "LIEFERUNG.POS_NR"), schema, Ct);

        Assert.Empty(partial.JumpsFor([Row(1m, 10m)], schema.OutgoingOf, schema.IncomingOf));
        var jump = Assert.Single(whole.JumpsFor([Row(10m, 2m)], schema.OutgoingOf, schema.IncomingOf));
        Assert.Equal("AUFTRAG_POSITION", jump.Jump.Table.Name);
        Assert.Equal(2, jump.Jump.Filters.Count);
    }

    [Fact]
    public async Task Relationships_of_the_CSharp_model_count_too()
    {
        var schema = await SchemaAsync();
        var sources = await SqlResultSources.LoadAsync("SELECT CHEF_ID FROM MITARBEITER", Columns("MITARBEITER.CHEF_ID"), schema, Ct);

        var jump = Assert.Single(sources.JumpsFor([Row(3m)], schema.OutgoingOf, schema.IncomingOf));
        Assert.Equal(FkSource.ClrModel, jump.Jump.ForeignKey.Source);
        Assert.Equal("ID = 3", jump.Jump.Condition);
    }

    [Fact]
    public async Task A_column_read_twice_gives_one_jump()
    {
        var schema = await SchemaAsync();
        var sources = await SqlResultSources.LoadAsync("SELECT \"a\".KUNDE_ID, KUNDE_ID FROM AUFTRAG \"a\"", Columns("a.KUNDE_ID", "AUFTRAG.KUNDE_ID"), schema, Ct);

        Assert.Equal(2, sources.Sources.Count);
        var jump = Assert.Single(sources.JumpsFor([Row(4711m, 4711m)], schema.OutgoingOf, schema.IncomingOf));
        Assert.True(jump.Involves(0));
        Assert.True(jump.Involves(1)); // the click on the second copy is meant just as well
    }

    [Fact]
    public async Task A_private_synonym_wins_over_a_public_one_of_the_same_name()
    {
        // Sorted by target owner the public one ("AAA") would come first.
        var publicOne = new TableDetails(new TableSummary("AAA", "LAND_ALT", TableKind.Table, new SynonymInfo(SynonymInfo.PublicOwner, "LAND")), [Text("CODE", 1)], [], [], false);
        var privateOne = new TableDetails(new TableSummary("ZZZ", "LAENDER", TableKind.Table, new SynonymInfo(Owner, "LAND")), [Text("CODE", 1)], [], [], false);

        var origins = SqlResultOrigins.Resolve("SELECT CODE FROM LAND", Columns("LAND.CODE"), await SchemaAsync(publicOne, privateOne));

        Assert.Equal("ZZZ.LAENDER", Assert.Single(origins)!.Table.Ref.ToString());
    }
}
