using FerretSharp.Core.ClrModel;
using FerretSharp.Core.Query;
using FerretSharp.Core.Schema;
using FerretSharp.Core.Workspaces;
using NSubstitute;

namespace FerretSharp.Core.Tests.Query;

public sealed class SqlBindsTests
{
    [Fact]
    public void Variables_become_typed_parameters_for_each_bind()
    {
        var spec = SqlBinds.Bind("SELECT * FROM t WHERE a = :id AND b = :Name AND c = :von AND d = :x AND e = :k AND f = :leer", ["id", "Name", "von", "x", "k", "leer"],
        [
            new SqlVariable("ID", SqlVariableType.Number, "1.234,5"),
            new SqlVariable("name", SqlVariableType.Text, "Meier"),
            new SqlVariable("von", SqlVariableType.Date, "05.10.2026 14:02"),
            new SqlVariable("x", SqlVariableType.Raw, "0xCAFE"),
            new SqlVariable("k", SqlVariableType.Char, "AB"),
            new SqlVariable("leer", SqlVariableType.Null, "egal"),
        ]);

        Assert.Equal(
            [
                new QueryParameter("id", 1234.5m, OracleTypeHint.Number),
                new QueryParameter("Name", "Meier", OracleTypeHint.Varchar2),
                new QueryParameter("von", new DateTime(2026, 10, 5, 14, 2, 0), OracleTypeHint.Date),
                new QueryParameter("k", "AB", OracleTypeHint.Char),
                new QueryParameter("leer", null, OracleTypeHint.Varchar2),
            ],
            spec.Parameters.Where(p => p.Name != "x"));
        Assert.Equal(new byte[] { 0xCA, 0xFE }, spec.Parameters.Single(p => p.Name == "x").Value);
    }

    [Theory]
    [InlineData(SqlVariableType.Number, "abc", ":id: „abc“ ist keine Zahl.")]
    [InlineData(SqlVariableType.Number, "", ":id: Zahl fehlt (für NULL den Typ NULL wählen).")]
    [InlineData(SqlVariableType.Date, "31.02.2026", ":id: „31.02.2026“ ist kein Datum (TT.MM.JJJJ [hh:mm[:ss]] oder ISO).")]
    [InlineData(SqlVariableType.Raw, "XYZ", ":id: „XYZ“ ist kein Hex-Wert (z. B. CAFE01).")]
    public void Values_that_do_not_fit_say_so(SqlVariableType type, string value, string message) =>
        Assert.Equal(message, Assert.Throws<SqlBindException>(() => SqlBinds.Bind("x", ["id"], [new SqlVariable("id", type, value)])).Message);

    [Fact]
    public void A_bind_without_variable_is_an_error() =>
        Assert.Equal("Für :id fehlt ein Wert.", Assert.Throws<SqlBindException>(() => SqlBinds.Bind("x", ["id"], [])).Message);

    [Fact]
    public void Reconcile_keeps_values_types_new_ones_and_unused_ones_at_the_end()
    {
        var existing = new[] { new SqlVariable("alt", SqlVariableType.Number, "1"), new SqlVariable("ID", SqlVariableType.Number, "4711") };

        var variables = SqlBinds.Reconcile(["id", "von"], existing, bind => bind == "von" ? SqlVariableType.Date : null);

        Assert.Equal(
            [
                new SqlVariable("ID", SqlVariableType.Number, "4711"),
                new SqlVariable("von", SqlVariableType.Date, ""),
                new SqlVariable("alt", SqlVariableType.Number, "1"),
            ],
            variables);
    }

    [Fact]
    public void Generated_parameters_become_variables_that_bind_the_same_values()
    {
        QueryParameter[] parameters =
        [
            new("p_0", 4711m, OracleTypeHint.Number),
            new("p_1", new DateTime(2026, 10, 5, 0, 0, 0), OracleTypeHint.Date),
            new("p_2", new DateTime(2026, 10, 5, 14, 2, 13, 500), OracleTypeHint.TimeStamp),
            new("p_3", "AB", OracleTypeHint.Char),
            new("p_4", new byte[] { 1, 2 }, OracleTypeHint.Raw),
            new("p_5", "%meier%", OracleTypeHint.Varchar2),
        ];

        var variables = parameters.Select(SqlBinds.FromParameter).ToList();
        var bound = SqlBinds.Bind("x", parameters.Select(p => p.Name).ToList(), variables).Parameters;

        Assert.Equal(parameters.Select(p => (p.Name, p.Value is byte[] b ? Convert.ToHexString(b) : p.Value, p.Type)),
            bound.Select(p => (p.Name, p.Value is byte[] b ? Convert.ToHexString(b) : p.Value, p.Type)));
    }
}

public sealed class SqlHistoryStoreTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("ferret-history-").FullName;
    private static readonly Guid Connection = Guid.NewGuid();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static SqlHistoryEntry Entry(string sql, params SqlVariable[] variables) =>
        new(sql, DateTimeOffset.Now, TimeSpan.FromMilliseconds(12), 3, false, false, null, variables);

    [Fact]
    public async Task Newest_first_repeats_replace_their_last_entry_and_values_survive()
    {
        var store = new SqlHistoryStore(_directory);
        await store.AddAsync(Connection, Entry("SELECT 1 FROM dual"), Ct);
        await store.AddAsync(Connection, Entry("SELECT 2 FROM dual", new SqlVariable("id", SqlVariableType.Number, "4711")), Ct);
        await store.AddAsync(Connection, Entry(" SELECT 2 FROM dual "), Ct);

        var history = await new SqlHistoryStore(_directory).LoadAsync(Connection, Ct);

        Assert.Equal([" SELECT 2 FROM dual ", "SELECT 1 FROM dual"], history.Select(e => e.Sql));
        Assert.Empty(await store.LoadAsync(Guid.NewGuid(), Ct));
    }

    [Fact]
    public async Task At_most_500_entries()
    {
        var store = new SqlHistoryStore(_directory);
        for (var i = 0; i < SqlHistoryStore.MaxEntries + 3; i++)
        {
            await store.AddAsync(Connection, Entry($"SELECT {i} FROM dual"), Ct);
        }

        var history = await store.LoadAsync(Connection, Ct);

        Assert.Equal(SqlHistoryStore.MaxEntries, history.Count);
        Assert.Equal($"SELECT {SqlHistoryStore.MaxEntries + 2} FROM dual", history[0].Sql);
    }

    [Fact]
    public async Task Variables_round_trip_and_prod_entries_keep_no_values()
    {
        var store = new SqlHistoryStore(_directory);
        var entry = Entry("SELECT * FROM t WHERE id = :id", new SqlVariable("id", SqlVariableType.Number, "4711"));
        await store.AddAsync(Connection, entry.WithoutValues(), Ct);

        var loaded = Assert.Single(await store.LoadAsync(Connection, Ct));

        Assert.Equal([new SqlVariable("id", SqlVariableType.Number, "")], loaded.Variables);
        store.Delete(Connection);
        Assert.Empty(await store.LoadAsync(Connection, Ct));
    }

    [Fact]
    public async Task A_broken_file_is_an_empty_history()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(Path.Combine(_directory, Connection.ToString("D") + ".json"), "{ kaputt", Ct);

        Assert.Empty(await new SqlHistoryStore(_directory).LoadAsync(Connection, Ct));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

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
        reader.GetSynonymTargetsAsync(Owner, Arg.Any<CancellationToken>()).Returns((IReadOnlyList<TableSummary>)[Synonym]);
        reader.GetForeignKeysAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((IReadOnlyList<ForeignKeyInfo>)[]);
        reader.GetDetailsAsync(Arg.Is<TableSummary>(t => t.Name == "KUNDEN"), Arg.Any<CancellationToken>()).Returns(Kunden);
        reader.GetDetailsAsync(Arg.Is<TableSummary>(t => t.Name == "AUFTRAG"), Arg.Any<CancellationToken>()).Returns(Auftrag);
        var schema = new SchemaCache(reader, Owner);
        await schema.LoadAsync(TestContext.Current.CancellationToken);
        return schema;
    }

    private static async Task<IReadOnlyList<SqlCompletionItem>> ItemsAsync(string marked)
    {
        var cursor = marked.IndexOf('|', StringComparison.Ordinal);
        return await SqlCompletion.ItemsAsync(marked.Remove(cursor, 1), cursor, await SchemaAsync(), TablePresentation.Plain,
            table => table.Name == "KUNDEN" ? "Kunde" : null, TestContext.Current.CancellationToken);
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
