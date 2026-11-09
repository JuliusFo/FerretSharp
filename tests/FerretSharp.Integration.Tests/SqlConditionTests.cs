using FerretSharp.Core.Connections;
using FerretSharp.Core.Data;
using FerretSharp.Core.Oracle;
using FerretSharp.Core.Query;
using FerretSharp.Core.Schema;

namespace FerretSharp.Integration.Tests;

/// <summary>
/// A table tab's condition written in SQL (WP-79) on a real database: it reads like the filter rows it was written from, with
/// subqueries, a comment at its end, in pages, counted and read whole – and Oracle's error for a column that is not there.
/// </summary>
public class SqlConditionTests(OracleContainerFixture oracle) : IAsyncLifetime
{
    private OracleSession? _session;
    private OracleDataAccess _data = null!;
    private TableDetails _grid = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        var (profile, password) = oracle.RequireProfile();
        await SampleSchema.EnsureCreatedAsync(oracle.RequireConnectionString(), Ct);
        _session = await OracleSession.OpenAsync(
            OracleConnectionStringFactory.Create(profile, password), new SessionContext("FerretSharp", "SQL condition tests"), Ct);
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

    /// <summary>
    /// "SQL condition" starts with the filter written as text: with literals instead of binds it finds the same rows – LIKE
    /// wildcards, "≠" with NULLs, CHAR padding, a whole day, a timestamp, a decimal comma, RAW and a NOT group.
    /// </summary>
    public static TheoryData<FilterCondition[]> Filters => new()
    {
        { [FilterCondition.Of("NAME", FilterOperator.Contains, "50%_")] },
        { [FilterCondition.Of("NAME", FilterOperator.NotEquals, "Name 7")] },
        { [FilterCondition.Of("KZ", FilterOperator.Equals, "AB")] },
        { [FilterCondition.Of("DATUM", FilterOperator.Equals, "2.1.2026")] },
        { [FilterCondition.Of("TS", FilterOperator.Between, "1.1.2026 01:00", "1.1.2026 02:00:00,123456")] },
        { [FilterCondition.Of("BETRAG", FilterOperator.Gt, "1.000,5")] },
        { [FilterCondition.Of("DATEN", FilterOperator.Equals, "CAFE")] },
        {
            [
                FilterCondition.Of("GRUPPE", FilterOperator.In, "1", "2"),
                FilterCondition.Of("NAME", FilterOperator.StartsWith, "Name 1") with { Group = new FilterGroup(1, FilterGroupKind.None) },
                FilterCondition.Of("KZ", FilterOperator.IsNotNull) with { Group = new FilterGroup(1, FilterGroupKind.None) },
            ]
        },
    };

    [Theory]
    [MemberData(nameof(Filters))]
    public async Task The_filter_written_as_a_condition_finds_the_same_rows(FilterCondition[] filters)
    {
        var condition = QueryBuilder.ConditionText(_grid, filters);

        var byRows = await _data.CountAsync(_grid, filters, Ct);

        Assert.True(byRows > 0, "the case finds no row");
        Assert.Equal(byRows, await _data.CountAsync(_grid, RowFilter.Where(condition), Ct));
    }

    /// <summary>
    /// The colleagues' case: IN on a subquery with another IN in it; the comment at the end comments out neither the order nor
    /// the page, and pages, the count and the whole read agree.
    /// </summary>
    [Fact]
    public async Task A_nested_in_with_a_comment_at_the_end_pages_counts_and_reads_whole()
    {
        var condition = RowFilter.Where(
            "ID IN (SELECT g.ID FROM GRID_TEST g\n WHERE g.GRUPPE IN (SELECT LEVEL FROM dual CONNECT BY LEVEL <= 2))\nAND NAME IS NOT NULL -- the colleagues' case");
        var sorts = new[] { new SortSpec("ID", true) };

        var count = await _data.CountAsync(_grid, condition, Ct);
        var paged = new List<RowData>();
        for (var offset = 0; ; offset += 100)
        {
            var page = await _data.ReadPageAsync(_grid, condition, sorts, new PageSpec(offset, 100), Ct);
            paged.AddRange(page.Rows);
            if (page.IsLastPage)
            {
                break;
            }
        }

        var whole = new List<RowData>();
        await _data.ReadAllAsync(_grid, condition, sorts, (rows, _, _) =>
        {
            whole.AddRange(rows);
            return Task.FromResult(true);
        }, Ct);

        var byRows = await _data.CountAsync(_grid,
            [FilterCondition.Of("GRUPPE", FilterOperator.In, "1", "2"), FilterCondition.Of("NAME", FilterOperator.IsNotNull)], Ct);
        Assert.Equal(byRows, count);
        Assert.Equal(count, paged.Count);
        var ids = paged.Select(r => r.Key).ToList();
        Assert.Equal(ids, whole.Select(r => r.Key));
        Assert.Equal(ids.Count, ids.Distinct().Count());
        var id = _grid.Columns.ToList().FindIndex(c => c.Name == "ID");
        Assert.Equal(paged.Select(r => (decimal)r.Values[id]!).OrderDescending(), paged.Select(r => (decimal)r.Values[id]!)); // ORDER BY ID DESC held
    }

    /// <summary>
    /// The check and the read-only tripwire take a line comment to run to the line feed (the SQL editor's tokenizer). Oracle
    /// must not end it earlier at a lone carriage return – the rest would be SQL both took for a comment (review of WP-79; a lone
    /// CR comes only from a file edited by hand, Monaco keeps line ends whole).
    /// </summary>
    [Fact]
    public async Task A_line_comment_ends_at_the_line_feed_for_oracle_too()
    {
        var count = await _data.CountAsync(_grid, RowFilter.Where("ID = 1 -- x\r OR 1 = 1"), Ct);

        Assert.Equal(1, count);
    }

    [Fact]
    public async Task A_column_that_is_not_there_is_oracle_s_error()
    {
        var error = await Assert.ThrowsAsync<DatabaseException>(() => _data.CountAsync(_grid, RowFilter.Where("NO_SUCH_COLUMN = 1"), Ct));

        Assert.Equal("ORA-00904", error.ErrorCode);
    }
}
