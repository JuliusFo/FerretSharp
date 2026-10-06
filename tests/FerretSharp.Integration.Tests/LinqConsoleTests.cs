using FerretSharp.Core.ClrModel;
using FerretSharp.Core.Data;
using FerretSharp.Core.Query;
using FerretSharp.Core.Schema;
using FerretSharp.Core.Settings;
using NSubstitute;

namespace FerretSharp.Integration.Tests;

/// <summary>
/// The LINQ console (ADR 0011) against the sample project: the host translates C# into the SQL EF would send – without
/// a database – and helps with queries copied from code. One console for all tests (starting it builds the model).
/// </summary>
public sealed class LinqConsoleTests : IAsyncLifetime
{
    private ILinqConsole _console = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        var link = ModelHostTests.SampleLink();
        _console = await ModelHostTests.Runner().StartConsoleAsync(link, BuildOutputLocator.Find(link), Ct);
    }

    public async ValueTask DisposeAsync() => await _console.DisposeAsync();

    [Fact]
    public async Task A_query_becomes_the_projects_sql_with_typed_parameters()
    {
        var result = await _console.RunAsync(
            "return await db.Kunden.Where(k => k.Kundenart == Kundenart.Gewerbe && k.KundeId > kundeId && k.Gesperrt).OrderBy(k => k.Name).Take(10).ToListAsync(ct);",
            "var kundeId = 4711;", Ct);

        Assert.False(result.HasErrors, string.Join("\n", result.Diagnostics));
        var command = Assert.Single(result.Commands);
        Assert.Equal(LinqProtocol.Reader, command.Kind);
        // The project's converters: the enum as its number, the bool as 'J'.
        Assert.Contains("\"KUNDENART\" = 2", command.Sql);
        Assert.Contains("\"GESPERRT\" = 'J'", command.Sql);
        Assert.Contains(new CapturedParameter("kundeId_0", "Int32", "Int32", "4711"), command.Parameters);
        Assert.Equal("List<Kunde>", result.ResultType);
        Assert.Equal(["db", "ct"], result.AutoDeclared);
    }

    [Fact]
    public async Task A_query_that_is_not_executed_is_enumerated_to_get_its_command()
    {
        var result = await _console.RunAsync("return db.Auftraege.Include(a => a.Kunde).Where(a => a.Status == AuftragStatus.Offen);", "", Ct);

        var command = Assert.Single(result.Commands);
        Assert.Contains("INNER JOIN \"KUNDEN\"", command.Sql);
        Assert.Contains("N'OFFEN'", command.Sql);
        Assert.Equal("IQueryable<Auftrag>", result.ResultType);
    }

    [Fact]
    public async Task Copied_code_names_the_unknown_names_with_typed_suggestions_and_does_not_run()
    {
        const string code = """
            var kunden = await _context.Kunden
                .Where(k => k.KundeId == customerId && k.ErstelltAm >= request.From && ids.Contains(k.KundeId))
                .ToListAsync(cancellationToken);
            return kunden;
            """;

        var result = await _console.RunAsync(code, "", Ct);

        Assert.Empty(result.Commands);
        Assert.Equal(["_context", "cancellationToken"], result.AutoDeclared);
        Assert.Equal(
            ["int customerId = 0;", "var request = new { From = DateTime.Today };", "List<int> ids = new List<int> { };"],
            result.UnknownNames.Select(u => u.Declaration));
        Assert.All(result.UnknownNames, u => Assert.True(u.TypeKnown));

        // As the user would: the suggestions taken, the list filled (empty, EF would turn the condition into 0 = 1).
        var variables = string.Join("\n", result.UnknownNames.Select(u => u.Declaration)).Replace("new List<int> { }", "new List<int> { 1, 2 }", StringComparison.Ordinal);
        var declared = await _console.RunAsync(code, variables, Ct);
        Assert.False(declared.HasErrors, string.Join("\n", declared.Diagnostics));
        var command = Assert.Single(declared.Commands);
        Assert.Contains(command.Parameters, p => p.Name.StartsWith("customerId", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Compiler_errors_point_into_the_users_sections()
    {
        var result = await _console.RunAsync("var x = 1;\nreturn db.Kunden.Where(k => k.Gibtsnicht == x);", "var y = ;", Ct);

        Assert.Empty(result.Commands);
        Assert.Contains(result.Diagnostics, d => d is { Section: LinqProtocol.VariablesSection, Line: 1, Severity: "error" });
        var missing = Assert.Single(result.Diagnostics, d => d.Id == "CS1061");
        Assert.Equal((LinqProtocol.CodeSection, 2), (missing.Section, missing.Line));
    }

    [Fact]
    public async Task Execute_update_and_count_are_captured_and_nothing_runs()
    {
        var update = await _console.RunAsync(
            "return await db.Auftraege.Where(a => a.KundeId == 5).ExecuteUpdateAsync(s => s.SetProperty(a => a.Status, AuftragStatus.Storniert), ct);", "", Ct);
        var count = await _console.RunAsync("return await db.Kunden.CountAsync(k => k.Name.StartsWith(\"M\"), ct);", "", Ct);

        var command = Assert.Single(update.Commands);
        Assert.Equal(LinqProtocol.NonQuery, command.Kind);
        Assert.StartsWith("UPDATE \"AUFTRAG\"", command.Sql);
        Assert.Contains("COUNT(*)", Assert.Single(count.Commands).Sql);
        // EF expected a row for COUNT; the empty answer of the capture is what it complains about – not shown as a failure.
        Assert.NotNull(count.Exception);
    }

    /// <summary>Completion (WP-19) with the cursor at <c>|</c> in the code, or in the variables if the marker is there.</summary>
    private async Task<IReadOnlyList<LinqCompletionItem>> CompleteAsync(string code, string variables = "")
    {
        var inVariables = variables.Contains('|', StringComparison.Ordinal);
        var marked = inVariables ? variables : code;
        var offset = marked.IndexOf('|', StringComparison.Ordinal);
        Assert.True(offset >= 0, "no cursor");
        var text = marked.Remove(offset, 1);
        return inVariables
            ? await _console.CompleteAsync(code, text, LinqProtocol.VariablesSection, offset, Ct)
            : await _console.CompleteAsync(text, variables, LinqProtocol.CodeSection, offset, Ct);
    }

    [Fact]
    public async Task After_the_context_come_its_dbsets_first()
    {
        var items = await CompleteAsync("return db.|");

        var kunden = Assert.Single(items, i => i.Label == "Kunden");
        Assert.Equal((LinqProtocol.Property, "DbSet<Kunde>", 0), (kunden.Kind, kunden.Detail, kunden.Rank));
        Assert.Contains(items, i => i.Label == "Auftraege" && i.Rank == 0);
        Assert.Contains(items, i => i is { Label: "SaveChanges", Kind: LinqProtocol.Method, Rank: 1 });
        Assert.DoesNotContain(items, i => i.Label.StartsWith("__", StringComparison.Ordinal));
    }

    [Fact]
    public async Task In_an_unfinished_lambda_come_the_entitys_properties()
    {
        var items = await CompleteAsync("return db.Kunden.Where(k => k.|\n");

        Assert.Equal("int", Assert.Single(items, i => i.Label == "KundeId").Detail);
        Assert.Equal("Kundenart", Assert.Single(items, i => i.Label == "Kundenart").Detail);
        Assert.Equal(LinqProtocol.Property, Assert.Single(items, i => i.Label == "Name").Kind);
        Assert.Contains(items, i => i is { Label: "ToString", Rank: 3 });
    }

    [Fact]
    public async Task A_typed_word_still_gets_the_members_and_linq_and_ef_extensions()
    {
        var items = await CompleteAsync("var x = 1;\nreturn await db.Kunden.Wh|.ToListAsync(ct);");

        var where = Assert.Single(items, i => i.Label == "Where");
        Assert.Equal((LinqProtocol.ExtensionMethod, 2), (where.Kind, where.Rank));
        Assert.Contains("(+", where.Detail, StringComparison.Ordinal);
        // The overload C# picks for a DbSet: Queryable's, with an expression.
        Assert.StartsWith("IQueryable<Kunde> Where<Kunde>(Expression<Func<Kunde, bool>> predicate)", where.Detail, StringComparison.Ordinal);
        Assert.Contains(items, i => i is { Label: "Include", Kind: LinqProtocol.ExtensionMethod });
        Assert.Contains(items, i => i is { Label: "ToListAsync", Kind: LinqProtocol.ExtensionMethod });
    }

    [Fact]
    public async Task After_an_enum_come_its_members_also_in_the_variables()
    {
        var code = await CompleteAsync("return db.Kunden.Where(k => k.Kundenart == Kundenart.|);");
        var variables = await CompleteAsync("return 1;", "var status = AuftragStatus.|");

        Assert.Equal("Kundenart = 2", Assert.Single(code, i => i.Label == "Gewerbe").Detail);
        Assert.All(code, i => Assert.Equal(LinqProtocol.EnumMember, i.Kind));
        Assert.Contains(variables, i => i is { Label: "Offen", Kind: LinqProtocol.EnumMember });
    }

    [Fact]
    public async Task Copied_code_gets_the_dbsets_after_an_undeclared_context()
    {
        var items = await CompleteAsync("var kunden = await _context.|\n    .ToListAsync(cancellationToken);");

        Assert.Contains(items, i => i.Label == "Kunden");
    }

    [Fact]
    public async Task Without_a_dot_come_variables_project_types_and_keywords()
    {
        var items = await CompleteAsync("return db.Kunden.Where(k => k.KundeId == ku|", "var kundeId = 4711;");

        Assert.Equal((LinqProtocol.Variable, "int"), (Assert.Single(items, i => i.Label == "kundeId").Kind, items.Single(i => i.Label == "kundeId").Detail));
        Assert.Contains(items, i => i.Label == "k" && i.Kind == LinqProtocol.Variable);
        Assert.Contains(items, i => i is { Label: "Kunde", Kind: LinqProtocol.Class });
        Assert.Contains(items, i => i is { Label: "Kundenart", Kind: LinqProtocol.Enum });
        Assert.Contains(items, i => i is { Label: "DateTime", Kind: LinqProtocol.Struct });
        Assert.Contains(items, i => i is { Label: "await", Kind: LinqProtocol.Keyword });
        Assert.DoesNotContain(items, i => i.Label is "Console" or "__Context");
    }

    [Fact]
    public async Task In_an_object_initializer_come_the_properties_not_set_yet()
    {
        var items = await CompleteAsync("var kunde = new Kunde { Name = \"Meier\", | };\nreturn kunde;");

        Assert.Contains(items, i => i.Label == "KundeId");
        Assert.DoesNotContain(items, i => i.Label == "Name");
        Assert.All(items, i => Assert.Contains(i.Kind, new[] { LinqProtocol.Property, LinqProtocol.Field }));
    }

    [Theory]
    [InlineData("return db.Kunden.Where(k => k.Name == \"db.|\");")]
    [InlineData("// db.|\nreturn 1;")]
    [InlineData("/* db.| */ return 1;")]
    [InlineData("var neu|")]
    public async Task Nothing_in_strings_comments_and_new_names(string code) => Assert.Empty(await CompleteAsync(code));

    /// <summary>
    /// Code generation (WP-15) against the real model: the LINQ for the grid's filters compiles in the console and EF
    /// sends the same conditions as the grid (case-insensitive LIKE, whole day, J/N); generated initializers compile.
    /// </summary>
    [Fact]
    public async Task Generated_filters_and_initializers_compile_against_the_project()
    {
        var presentation = await SampleKundenAsync();
        var filters = new[]
        {
            FilterCondition.Of("NAME", FilterOperator.Contains, "meier"),
            FilterCondition.Of("ERSTELLT_AM", FilterOperator.Equals, "01.10.2026"),
            FilterCondition.Of("KUNDENART", FilterOperator.In, "1", "3"),
            FilterCondition.Of("GESPERRT", FilterOperator.Equals, "J"),
            FilterCondition.Of("UMSATZ", FilterOperator.Gt, "1.000,5"),
        };

        var query = LinqFilter.Build(presentation, filters, LinqFilter.Source(presentation));
        Assert.Empty(query.Warnings);
        var result = await _console.RunAsync(query.Text, "", Ct);

        Assert.False(result.HasErrors, string.Join("\n", result.Diagnostics));
        var sql = Assert.Single(result.Commands).Sql;
        Assert.Contains("UPPER(", sql);
        Assert.Contains("\"GESPERRT\" = 'J'", sql);
        Assert.Contains("\"KUNDENART\" IN (1, 3)", sql);

        var row = new RowData(new RowKey.PrimaryKey([4711m]),
            [4711m, "Meier \"GmbH\"\r\nZweigstelle", "ABC", new DateTime(2026, 10, 5, 14, 2, 13), 1234.50m, 7m, "J", 3m, 12m]);
        var initializer = CSharpRows.Initializers(presentation, [row, row]);
        Assert.Equal(["ANZAHL: keine Property (2 Zeilen) – als Kommentar."], initializer.Warnings); // in the table, not in the entity
        var compiled = await _console.RunAsync(initializer.Text + "\nreturn kunden.Count;", "", Ct);
        Assert.False(compiled.HasErrors, initializer.Text + "\n" + string.Join("\n", compiled.Diagnostics));
        Assert.Equal("int", compiled.ResultType);
    }

    /// <summary>KUNDEN of the sample database (tools/sample-db 01 + 05) with the model the host exports.</summary>
    private static async Task<TablePresentation> SampleKundenAsync()
    {
        var link = ModelHostTests.SampleLink();
        var model = (await ModelHostTests.Runner().ReadModelAsync(link, BuildOutputLocator.Find(link), Ct)).Model!;
        const string owner = "APP";
        var kunden = new TableDetails(new TableSummary(owner, "KUNDEN", TableKind.Table),
            [
                new("KUNDE_ID", "NUMBER", null, false, 10, 0, false, false, null, 1),
                new("NAME", "VARCHAR2", 100, true, null, null, false, false, null, 2),
                new("KUERZEL", "CHAR", 3, false, null, null, true, false, null, 3),
                new("ERSTELLT_AM", "DATE", null, false, null, null, false, false, null, 4),
                new("UMSATZ", "NUMBER", null, false, 12, 2, true, false, null, 5),
                new("ANZAHL", "NUMBER", null, false, 38, 0, true, false, null, 6),
                new("GESPERRT", "CHAR", 1, false, null, null, false, false, null, 7),
                new("KUNDENART", "NUMBER", null, false, 2, 0, false, false, null, 8),
                new("ADRESSE_ID", "NUMBER", null, false, 10, 0, true, false, null, 9),
            ],
            ["KUNDE_ID"], [], false);
        var reader = Substitute.For<ISchemaReader>();
        reader.GetTablesAsync(owner, Arg.Any<CancellationToken>()).Returns((IReadOnlyList<TableSummary>)[kunden.Table]);
        reader.GetSynonymTargetsAsync(owner, Arg.Any<CancellationToken>()).Returns((IReadOnlyList<TableSummary>)[]);
        reader.GetForeignKeysAsync(owner, Arg.Any<CancellationToken>()).Returns((IReadOnlyList<ForeignKeyInfo>)[]);
        reader.GetColumnNamesAsync(owner, Arg.Any<CancellationToken>()).Returns((IReadOnlyDictionary<string, IReadOnlyList<string>>)
            new Dictionary<string, IReadOnlyList<string>> { ["KUNDEN"] = kunden.Columns.Select(c => c.Name).ToList() });
        var schema = new SchemaCache(reader, owner);
        await schema.LoadAsync(Ct);
        var mapping = await ClrModelMapping.BuildAsync(model, schema, Ct);
        return TablePresentation.Create(kunden, mapping, ClrNameDisplay.Beside);
    }
}
