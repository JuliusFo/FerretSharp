using FerretSharp.Core.ClrModel;

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
}
