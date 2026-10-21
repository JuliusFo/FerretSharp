using FerretSharp.Core.Oracle;
using FerretSharp.Core.Query;
using FerretSharp.Core.Schema;

namespace FerretSharp.Integration.Tests;

/// <summary>
/// Filter groups on a real database (WP-50): NOT counts a row whose condition is unknown (NULL) as "does not apply",
/// as C# does – so an OR group and the NOT group of the same rows split the table without loss.
/// </summary>
public class FilterGroupTests(OracleContainerFixture oracle) : IAsyncLifetime
{
    private const int RowCount = 1234;

    private OracleSession? _session;
    private OracleDataAccess _data = null!;
    private TableDetails _grid = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        var (profile, password) = oracle.RequireProfile();
        await SampleSchema.EnsureCreatedAsync(oracle.RequireConnectionString(), Ct);
        _session = await OracleSession.OpenAsync(
            OracleConnectionStringFactory.Create(profile, password), new SessionContext("FerretSharp", "Filter group tests"), Ct);
        _data = new OracleDataAccess(_session);
        _grid = await new OracleSchemaReader(_session).GetDetailsAsync(new TableSummary(profile.EffectiveSchema, "GRID_TEST", TableKind.Table), Ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_session is not null)
        {
            await _session.DisposeAsync();
        }
    }

    private async Task<int> CountAsync(params FilterCondition[] filters) => (int)await _data.CountAsync(_grid, filters, Ct);

    private static FilterCondition[] Group(FilterGroupKind kind, params FilterCondition[] filters) =>
        [.. filters.Select(f => f with { Group = new FilterGroup(1, kind) })];

    /// <summary>
    /// Every operator on columns with NULLs (NAME, KZ, NOTIZ, DATEN) and without (GRUPPE is NOT NULL, DATUM holds none).
    /// A row alone in a NOT group takes the plain NOT for NULL checks and "≠" on a nullable column, CASE otherwise.
    /// </summary>
    public static TheoryData<string, FilterOperator, string[]> Conditions => new()
    {
        { "NAME", FilterOperator.Equals, ["Name 7"] },
        { "NAME", FilterOperator.NotEquals, ["Name 7"] },
        { "NAME", FilterOperator.Contains, ["%_"] },
        { "NAME", FilterOperator.StartsWith, ["Name 1"] },
        { "NAME", FilterOperator.EndsWith, ["1"] },
        { "NAME", FilterOperator.Gt, ["Name 5"] },
        { "NAME", FilterOperator.Gte, ["Name 5"] },
        { "NAME", FilterOperator.Lt, ["Name 5"] },
        { "NAME", FilterOperator.Lte, ["Name 5"] },
        { "NAME", FilterOperator.Between, ["Name 2", "Name 4"] },
        { "NAME", FilterOperator.In, ["Name 1", "Name 2", "50 Rabatt"] },
        { "NAME", FilterOperator.IsNull, [] },
        { "NAME", FilterOperator.IsNotNull, [] },
        { "KZ", FilterOperator.Equals, ["AB"] },
        { "KZ", FilterOperator.EndsWith, ["B"] },
        { "NOTIZ", FilterOperator.Contains, ["x"] },
        { "DATEN", FilterOperator.Equals, ["CAFE"] },
        { "GRUPPE", FilterOperator.Equals, ["3"] },
        { "DATUM", FilterOperator.Equals, ["2.1.2026"] },
        { "DATUM", FilterOperator.Between, ["2.1.2026", "4.1.2026"] },
    };

    [Theory]
    [MemberData(nameof(Conditions))]
    public async Task Or_and_not_of_the_same_rows_split_the_table(string column, FilterOperator op, string[] values)
    {
        var condition = new FilterCondition(column, op, values);
        var other = FilterCondition.Of("BETRAG", FilterOperator.Lt, "30"); // never NULL (LEVEL * 1.5): rows 1 … 19

        var alone = await CountAsync(condition);
        var any = await CountAsync(Group(FilterGroupKind.Any, condition, other));
        var none = await CountAsync(Group(FilterGroupKind.None, condition, other));
        var notAlone = await CountAsync(Group(FilterGroupKind.None, condition));

        Assert.Equal(RowCount, any + none);
        Assert.Equal(RowCount, alone + notAlone);
        Assert.Equal(alone + 19 - await CountAsync(condition, other), any);
    }

    [Fact]
    public async Task Not_keeps_the_rows_whose_value_is_null()
    {
        // Every 10th NAME is NULL: "NOT (NAME = 'Name 7')" in plain SQL would lose those 123 rows.
        var none = await CountAsync(Group(FilterGroupKind.None, FilterCondition.Of("NAME", FilterOperator.Equals, "Name 7")));

        Assert.Equal(RowCount - 1, none);
    }

    [Fact]
    public async Task Groups_combine_with_and_with_the_other_rows()
    {
        var count = await CountAsync([
            FilterCondition.Of("GRUPPE", FilterOperator.Equals, "3"),
            .. Group(FilterGroupKind.Any, FilterCondition.Of("NAME", FilterOperator.IsNull), FilterCondition.Of("ID", FilterOperator.Lt, "100")),
        ]);

        Assert.Equal(await CountAsync(FilterCondition.Of("GRUPPE", FilterOperator.Equals, "3"), FilterCondition.Of("NAME", FilterOperator.IsNull))
                     + await CountAsync(FilterCondition.Of("GRUPPE", FilterOperator.Equals, "3"), FilterCondition.Of("ID", FilterOperator.Lt, "100"))
                     - await CountAsync(FilterCondition.Of("GRUPPE", FilterOperator.Equals, "3"), FilterCondition.Of("NAME", FilterOperator.IsNull), FilterCondition.Of("ID", FilterOperator.Lt, "100")),
            count);
    }
}
