using FerretSharp.Core.Data;
using FerretSharp.Core.Oracle;
using FerretSharp.Core.Query;
using FerretSharp.Core.Schema;

namespace FerretSharp.Integration.Tests;

public class OracleDataAccessTests(OracleContainerFixture oracle) : IAsyncLifetime
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
            OracleConnectionStringFactory.Create(profile, password), new SessionContext("FerretSharp", "Data tests"), Ct);
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

    private int Index(string column) => _grid.Columns.ToList().FindIndex(c => c.Name == column);

    private async Task<List<RowData>> ReadAllAsync(IReadOnlyList<FilterCondition> filters, IReadOnlyList<SortSpec> sorts, int pageSize)
    {
        var rows = new List<RowData>();
        for (var offset = 0; ; offset += pageSize)
        {
            var page = await _data.ReadPageAsync(_grid, filters, sorts, new PageSpec(offset, pageSize), Ct);
            rows.AddRange(page.Rows);
            if (page.IsLastPage)
            {
                return rows;
            }
        }
    }

    private async Task<int> CountAsync(params FilterCondition[] filters) => (int)await _data.CountAsync(_grid, filters, Ct);

    [Theory]
    [InlineData("GRUPPE", false)]
    [InlineData("GRUPPE", true)]
    [InlineData("NAME", false)] // with NULLs
    public async Task Pages_neither_overlap_nor_leave_gaps_when_sorting_by_columns_with_ties(string column, bool descending)
    {
        var rows = await ReadAllAsync([], [new SortSpec(column, descending)], pageSize: 97);

        var ids = rows.Select(r => (decimal)r.Values[Index("ID")]!).ToList();
        Assert.Equal(RowCount, ids.Count);
        Assert.Equal(RowCount, ids.Distinct().Count());
    }

    [Fact]
    public async Task Rows_carry_primary_key_and_exact_values()
    {
        var page = await _data.ReadPageAsync(_grid, [], [], new PageSpec(0, 3), Ct);
        var first = page.Rows[0];
        decimal Id(RowData r) => (decimal)r.Values[Index("ID")]!;

        Assert.Equal([1m, 2m, 3m], page.Rows.Select(Id));
        Assert.Equal(new RowKey.PrimaryKey([1m]), first.Key);
        Assert.Equal(1.5m, first.Values[Index("BETRAG")]);
        Assert.Equal(new BigNumber("12345678901234567890123456789012345678"), first.Values[Index("RIESIG")]);
        Assert.Equal(new DateTime(2026, 1, 1), first.Values[Index("DATUM")]);
        Assert.Equal(new DateTime(2026, 1, 1, 0, 1, 0).AddTicks(1234560), first.Values[Index("TS")]);
        Assert.Equal(new byte[] { 0xCA, 0xFE }, first.Values[Index("DATEN")]);
        Assert.Equal("AB ", first.Values[Index("KZ")]);

        var clob = Assert.IsType<LobValue>(first.Values[Index("NOTIZ")]);
        Assert.Equal(3000, clob.Length);
        Assert.Equal(QueryBuilder.ClobPreviewLength, clob.Preview!.Length);
        Assert.Equal(new LobValue("", 0), page.Rows[1].Values[Index("NOTIZ")]);
        Assert.Null(page.Rows[2].Values[Index("NOTIZ")]);
        Assert.False(page.IsLastPage);
    }

    [Fact]
    public async Task Like_filters_treat_wildcards_literally_and_ignore_case()
    {
        Assert.Equal(1, await CountAsync(FilterCondition.Of("NAME", FilterOperator.Contains, "50%_")));
        Assert.Equal(2, await CountAsync(FilterCondition.Of("NAME", FilterOperator.StartsWith, "50")));
        Assert.Equal(1, await CountAsync(FilterCondition.Of("NAME", FilterOperator.Equals, "Name 7")));
        Assert.Equal(RowCount - 123 - 2, await CountAsync(FilterCondition.Of("NAME", FilterOperator.StartsWith, "name ")));
    }

    [Fact]
    public async Task Null_semantics_match_linq()
    {
        var nulls = RowCount / 10;
        Assert.Equal(nulls, await CountAsync(FilterCondition.Of("NAME", FilterOperator.IsNull)));
        Assert.Equal(RowCount - 1, await CountAsync(FilterCondition.Of("NAME", FilterOperator.NotEquals, "Name 7"))); // includes NULLs
    }

    [Fact]
    public async Task Char_columns_match_without_trailing_blanks()
    {
        Assert.Equal(3, await CountAsync(FilterCondition.Of("KZ", FilterOperator.Equals, "AB")));
    }

    [Fact]
    public async Task Date_without_time_matches_the_whole_day()
    {
        // DATUM = 2026-01-01 + (n-1)/4 → four rows per day (00:00, 06:00, 12:00, 18:00).
        Assert.Equal(4, await CountAsync(FilterCondition.Of("DATUM", FilterOperator.Equals, "2.1.2026")));
        Assert.Equal(1, await CountAsync(FilterCondition.Of("DATUM", FilterOperator.Equals, "02.01.2026 06:00")));
        Assert.Equal(8, await CountAsync(FilterCondition.Of("DATUM", FilterOperator.Between, "1.1.2026", "2.1.2026")));
        Assert.Equal(4, await CountAsync(FilterCondition.Of("DATUM", FilterOperator.Lte, "1.1.2026")));
    }

    [Fact]
    public async Task Numbers_and_large_in_lists_work()
    {
        Assert.Equal(1, await CountAsync(FilterCondition.Of("BETRAG", FilterOperator.Equals, "1,5")));
        Assert.Equal(RowCount - 10, await CountAsync(FilterCondition.Of("ID", FilterOperator.Gt, "10")));

        var ids = Enumerable.Range(1, 1500).Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        Assert.Equal(RowCount, await CountAsync(FilterCondition.Of("ID", FilterOperator.In, ids)));
    }

    [Fact]
    public async Task View_without_key_can_be_paged()
    {
        var owner = _grid.Table.Owner;
        var view = await new OracleSchemaReader(_session!).GetDetailsAsync(new TableSummary(owner, "V_KUNDEN_AUFTRAEGE", TableKind.View), Ct);

        var page = await _data.ReadPageAsync(view, [], [], new PageSpec(0, 10), Ct);

        Assert.True(page.IsLastPage);
        Assert.All(page.Rows, r => Assert.IsType<RowKey.None>(r.Key));
    }
}
