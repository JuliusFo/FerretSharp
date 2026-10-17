using FerretSharp.Core.ClrModel;
using FerretSharp.Core.Data;
using FerretSharp.Core.Oracle;
using FerretSharp.Core.Query;
using FerretSharp.Core.Schema;
using FerretSharp.Core.Settings;
using Oracle.ManagedDataAccess.Client;

namespace FerretSharp.Integration.Tests;

/// <summary>
/// Review 3.40.0, C2: the filter groups of WP-50 as LINQ (<see cref="LinqFilter"/>) keep the rows the grid keeps – also in
/// a NOT group over columns with NULLs, where EF Core adds no NULL handling of its own to a negated &lt; or OR. The LINQ
/// runs in the console of the sample project (EF Core 8 with the Oracle provider), its SQL against rows with NULLs; the
/// grid's SQL (<see cref="QueryBuilder"/>) reads the same rows.
/// </summary>
public sealed class LinqFilterGroupTests(OracleContainerFixture oracle) : IAsyncLifetime
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static bool _created;

    // The sample model's KUNDEN (Kunde.Kuerzel, Kunde.Umsatz are nullable) – rows of our own, so the sample's stay as they are.
    private const string Table = "LG_KUNDEN";

    private static readonly string[] Setup =
    [
        $"CREATE TABLE {Table} (KUNDE_ID NUMBER(10) PRIMARY KEY, NAME VARCHAR2(100 CHAR) NOT NULL, KUERZEL CHAR(3 BYTE), UMSATZ NUMBER(12,2))",
        $"INSERT INTO {Table} VALUES (1, 'a', 'ABC', 1000)",
        $"INSERT INTO {Table} VALUES (2, 'b', NULL, 1000)",
        $"INSERT INTO {Table} VALUES (3, 'c', 'XYZ', NULL)",
        $"INSERT INTO {Table} VALUES (4, 'd', NULL, NULL)",
        $"INSERT INTO {Table} VALUES (5, 'e', 'XYZ', 9000)",
        $"INSERT INTO {Table} VALUES (6, 'f', 'ABC', 9000)",
        "COMMIT",
    ];

    private OracleSession? _session;
    private OracleDataAccess _data = null!;
    private TableDetails _rows = null!;
    private TablePresentation _kunden = null!;
    private ILinqConsole? _console;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        var (profile, password) = oracle.RequireProfile();
        var link = ModelHostTests.SampleLink(); // skips without the .NET 8 runtime
        await SampleSchema.EnsureCreatedAsync(oracle.RequireConnectionString(), Ct);
        await Gate.WaitAsync(Ct);
        try
        {
            if (!_created)
            {
                await using var connection = new OracleConnection(oracle.RequireConnectionString());
                await connection.OpenAsync(Ct);
                foreach (var statement in Setup)
                {
                    await using var command = connection.CreateCommand();
                    command.CommandText = statement;
                    await command.ExecuteNonQueryAsync(Ct);
                }

                _created = true;
            }
        }
        finally
        {
            Gate.Release();
        }

        _session = await OracleSession.OpenAsync(
            OracleConnectionStringFactory.Create(profile, password), new SessionContext("FerretSharp", "LINQ filter groups"), Ct);
        _data = new OracleDataAccess(_session);
        var schema = new SchemaCache(new OracleSchemaReader(_session), profile.EffectiveSchema);
        await schema.LoadAsync(Ct);
        _rows = await schema.GetDetailsAsync(schema.Find(new TableRef(schema.Owner, Table))!, Ct);

        var model = (await ModelHostTests.Runner().ReadModelAsync(link, BuildOutputLocator.Find(link), Ct)).Model!;
        var mapping = await ClrModelMapping.BuildAsync(model, schema, Ct);
        _kunden = TablePresentation.Create(await schema.GetDetailsAsync(schema.Find(new TableRef(schema.Owner, "KUNDEN"))!, Ct), mapping, ClrNameDisplay.Beside);
        _console = await ModelHostTests.Runner().StartConsoleAsync(link, BuildOutputLocator.Find(link), Ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_console is not null)
        {
            await _console.DisposeAsync();
        }

        if (_session is not null)
        {
            await _session.DisposeAsync();
        }
    }

    private static FilterCondition[] Group(FilterGroupKind kind, params FilterCondition[] filters) =>
        [.. filters.Select(f => f with { Group = new FilterGroup(1, kind) })];

    public static TheoryData<string, FilterCondition[]> Filters => new()
    {
        { "NOT (Umsatz < 5000)", Group(FilterGroupKind.None, FilterCondition.Of("UMSATZ", FilterOperator.Lt, "5000")) },
        { "NOT (Umsatz < 5000 OR Kuerzel = ABC)", Group(FilterGroupKind.None, FilterCondition.Of("UMSATZ", FilterOperator.Lt, "5000"), FilterCondition.Of("KUERZEL", FilterOperator.Equals, "ABC")) },
        { "NOT (Kuerzel ≠ ABC)", Group(FilterGroupKind.None, FilterCondition.Of("KUERZEL", FilterOperator.NotEquals, "ABC")) },
        { "NOT (Umsatz between)", Group(FilterGroupKind.None, new FilterCondition("UMSATZ", FilterOperator.Between, ["500", "5000"])) },
        { "NOT (Kuerzel in)", Group(FilterGroupKind.None, new FilterCondition("KUERZEL", FilterOperator.In, ["ABC", "XYZ"])) },
        { "OR (Umsatz > 5000, Kuerzel is NULL)", Group(FilterGroupKind.Any, FilterCondition.Of("UMSATZ", FilterOperator.Gt, "5000"), new FilterCondition("KUERZEL", FilterOperator.IsNull, [])) },
    };

    [Theory]
    [MemberData(nameof(Filters))]
    public async Task Linq_keeps_the_rows_the_grid_keeps(string name, FilterCondition[] filters)
    {
        var linq = LinqFilter.Build(_kunden, filters, LinqFilter.Source(_kunden));
        Assert.Empty(linq.Warnings);

        var result = await _console!.RunAsync($"return await {linq.Text}.Select(k => k.KundeId).ToListAsync(ct);", "", Ct);

        Assert.False(result.HasErrors, string.Join("\n", result.Diagnostics));
        var command = Assert.Single(result.Commands);
        var query = LinqStatements.ToQuery(command);
        var ef = await _data.ReadSqlAsync(query with { Sql = query.Sql.Replace("\"KUNDEN\"", $"\"{Table}\"", StringComparison.Ordinal) }, 0, 100, Ct);
        var grid = await _data.ReadPageAsync(_rows, filters, [], new PageSpec(0, 100), Ct);

        Assert.Equal(grid.Rows.Select(r => Convert.ToInt32(r.Values[0])).Order(), ef.Rows.Select(r => Convert.ToInt32(r[0])).Order());
        Assert.True(grid.Rows.Count > 0, name); // the case compares rows, not two empty results
    }
}
