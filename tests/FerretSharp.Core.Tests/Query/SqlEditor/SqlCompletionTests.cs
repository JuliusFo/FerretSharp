using FerretSharp.Core.ClrModel;
using FerretSharp.Core.Query;
using FerretSharp.Core.Schema;
using FerretSharp.Core.Workspaces;
using NSubstitute;

namespace FerretSharp.Core.Tests.Query;

public sealed class SqlCompletionTests
{
    private const string Owner = "FERRET";

    private static readonly TableDetails Kunden = new(new TableSummary(Owner, "KUNDEN", TableKind.Table),
        [
            new("KUNDE_ID", "NUMBER", null, false, 10, 0, false, false, null, 1),
            new("NAME", "VARCHAR2", 100, true, null, null, false, false, null, 2),
            new("DATE", "DATE", null, false, null, null, true, false, null, 3),
            new("Mixed", "VARCHAR2", 10, true, null, null, true, false, null, 4),
        ],
        ["KUNDE_ID"], [], false);

    private static readonly TableDetails Auftrag = new(new TableSummary(Owner, "AUFTRAG", TableKind.Table),
        [new("AUFTRAG_ID", "NUMBER", null, false, 10, 0, false, false, null, 1), new("KUNDE_ID", "NUMBER", null, false, 10, 0, false, false, null, 2)],
        ["AUFTRAG_ID"], [], false);

    private static readonly TableSummary Fremd = new("ANDERE", "PREISE", TableKind.View);
    private static readonly TableSummary Synonym = new("ANDERE", "LAENDER", TableKind.Table, new SynonymInfo(SynonymInfo.PublicOwner, "LAND"));

    private static async Task<SchemaCache> SchemaAsync()
    {
        var reader = Substitute.For<ISchemaReader>();
        reader.GetTablesAsync(Owner, Arg.Any<CancellationToken>()).Returns((IReadOnlyList<TableSummary>)[Kunden.Table, Auftrag.Table]);
        reader.GetSynonymTargetsAsync(Owner, Arg.Any<CancellationToken>()).Returns(new SynonymTargets([Synonym], []));
        reader.GetForeignKeysAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>()).Returns((IReadOnlyList<ForeignKeyInfo>)[]);
        reader.GetDetailsAsync(Arg.Is<TableSummary>(t => t.Name == "KUNDEN"), Arg.Any<CancellationToken>()).Returns(Kunden);
        reader.GetDetailsAsync(Arg.Is<TableSummary>(t => t.Name == "AUFTRAG"), Arg.Any<CancellationToken>()).Returns(Auftrag);
        var schema = new SchemaCache(reader, Owner);
        await schema.LoadAsync(TestContext.Current.CancellationToken);
        return schema;
    }

    private static async Task<IReadOnlyList<SqlCompletionItem>> ItemsAsync(string marked)
    {
        var cursor = marked.IndexOf('|', StringComparison.Ordinal);
        var text = marked.Remove(cursor, 1);
        return await SqlCompletion.ItemsAsync(text, cursor, SqlCompletion.AtLineEnd(text, cursor), await SchemaAsync(), TablePresentation.Plain,
            table => table.Name == "KUNDEN" ? "Kunde" : null, TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData("SELECT * FROM ku|", true)]
    [InlineData("SELECT * FROM ku|\r\nWHERE 1 = 1", true)]
    [InlineData("SELECT * FROM ku|\nWHERE 1 = 1", true)]
    [InlineData("SELECT * FROM ku| WHERE 1 = 1", false)]
    [InlineData("SELECT * FROM ku|;", false)]
    [InlineData("SELECT * FROM ku|nden", false)]
    [InlineData("SELECT * FROM ku|)", false)]
    public void A_space_follows_only_at_the_end_of_a_line(string marked, bool expected)
    {
        var cursor = marked.IndexOf('|', StringComparison.Ordinal);
        Assert.Equal(expected, SqlCompletion.AtLineEnd(marked.Remove(cursor, 1), cursor));
    }

    [Fact]
    public async Task Tables_and_clause_keywords_end_with_a_space_at_the_end_of_a_line()
    {
        var tables = await ItemsAsync("SELECT * FROM ku|");
        var elsewhere = await ItemsAsync("SELECT * FROM kunden WHERE |");

        Assert.All(tables, i => Assert.EndsWith(" ", i.InsertText, StringComparison.Ordinal));
        Assert.Equal("KUNDEN ", tables.Single(i => i.Label == "KUNDEN").InsertText);
        Assert.Equal("ORDER BY ", elsewhere.Single(i => i.Label == "ORDER BY").InsertText);
        Assert.Equal("IS NOT NULL ", elsewhere.Single(i => i.Label == "IS NOT NULL").InsertText);
    }

    [Fact]
    public async Task Columns_values_and_functions_get_no_space()
    {
        var items = await ItemsAsync("SELECT * FROM kunden WHERE |");

        Assert.All(items.Where(i => i.Kind == SqlCompletionKind.Column), i => Assert.DoesNotContain(" ", i.InsertText, StringComparison.Ordinal));
        Assert.Equal(["NULL", "DESC", "COUNT(*)", "SYSDATE", "NVL"],
            new[] { "NULL", "DESC", "COUNT(*)", "SYSDATE", "NVL" }.Select(k => items.Single(i => i.Label == k).InsertText));
    }

    [Fact]
    public async Task Nothing_gets_a_space_when_text_follows_on_the_line()
    {
        var items = await ItemsAsync("SELECT * FROM ku| WHERE 1 = 1");

        Assert.Equal("KUNDEN", items.Single(i => i.Label == "KUNDEN").InsertText);
    }

    [Theory]
    [InlineData("SELECT * FROM |")]
    [InlineData("SELECT * FROM ku|")]
    [InlineData("SELECT * FROM kunden k JOIN |")]
    [InlineData("SELECT * FROM kunden k, |")]
    [InlineData("UPDATE |")]
    [InlineData("INSERT INTO |")]
    public async Task After_from_join_into_come_tables(string marked)
    {
        var items = await ItemsAsync(marked);

        Assert.Equal(["AUFTRAG", "KUNDEN", "LAND"], items.Select(i => i.Label).Order(StringComparer.Ordinal));
        Assert.Equal("Tabelle · Entity Kunde", items.Single(i => i.Label == "KUNDEN").Detail);
        Assert.Equal((SqlCompletionKind.Synonym, "Synonym → ANDERE.LAENDER (öffentlich)"), (items.Single(i => i.Label == "LAND").Kind, items.Single(i => i.Label == "LAND").Detail));
    }

    [Fact]
    public async Task After_an_alias_and_a_dot_come_its_columns_quoted_where_needed()
    {
        var items = await ItemsAsync("SELECT k.| FROM kunden k JOIN auftrag a ON a.kunde_id = k.kunde_id");

        Assert.Equal(["KUNDE_ID", "NAME", "\"DATE\"", "\"Mixed\""], items.Select(i => i.InsertText));
        Assert.Equal("NUMBER(10)", items[0].Detail);
        Assert.All(items, i => Assert.Equal(SqlCompletionKind.Column, i.Kind));
    }

    [Fact]
    public async Task Elsewhere_come_the_columns_of_all_named_tables_then_keywords()
    {
        var items = await ItemsAsync("SELECT * FROM kunden k JOIN auftrag a ON a.kunde_id = k.kunde_id WHERE |");

        Assert.Equal(6, items.Count(i => i.Kind == SqlCompletionKind.Column));
        Assert.Equal("KUNDEN · NUMBER(10)", items.First(i => i.Label == "KUNDE_ID").Detail);
        Assert.Contains(items, i => i is { Kind: SqlCompletionKind.Keyword, Label: "ORDER BY" });
        Assert.DoesNotContain(items, i => i.Kind == SqlCompletionKind.Table);
    }

    [Theory]
    [InlineData("SELECT * FROM kunden WHERE name = 'FROM |")]
    [InlineData("SELECT * FROM kunden -- FROM |")]
    [InlineData("SELECT * FROM kunden /* | */")]
    public async Task Nothing_inside_strings_and_comments(string marked) => Assert.Empty(await ItemsAsync(marked));

    [Fact]
    public void Names_are_quoted_only_where_oracle_needs_it() =>
        Assert.Equal(["KUNDEN", "\"Kunden\"", "\"DATE\"", "\"A B\"", "K$1"],
            new[] { "KUNDEN", "Kunden", "DATE", "A B", "K$1" }.Select(SqlCompletion.Quote));
}
