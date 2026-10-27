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
    public async Task Descending_text_sort_is_binary()
    {
        // Binary: 'N' (0x4E) > '5' (0x35). A German linguistic sort would put "50…" first.
        var page = await _data.ReadPageAsync(_grid, [FilterCondition.Of("NAME", FilterOperator.IsNotNull)], [new SortSpec("NAME", true)], new PageSpec(0, 1), Ct);

        Assert.StartsWith("Name ", (string)page.Rows[0].Values[Index("NAME")]!);
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

    // ---------- whole reads for the export of all rows (WP-54) ----------

    [Fact]
    public async Task A_whole_read_gives_the_rows_of_the_pages_in_blocks()
    {
        IReadOnlyList<FilterCondition> filters = [FilterCondition.Of("GRUPPE", FilterOperator.NotEquals, "3")];
        IReadOnlyList<SortSpec> sorts = [new SortSpec("GRUPPE", true)];
        var blocks = new List<IReadOnlyList<RowData>>();

        await _data.ReadAllAsync(_grid, filters, sorts, (rows, _, _) =>
        {
            blocks.Add(rows);
            return Task.FromResult(true);
        }, Ct);

        var paged = await ReadAllAsync(filters, sorts, pageSize: 500);
        Assert.Equal(paged.Count, blocks.Sum(b => b.Count));
        Assert.All(blocks.SkipLast(1), b => Assert.Equal(IDataAccess.BlockSize, b.Count));
        Assert.Equal(paged.Select(r => r.Key), blocks.SelectMany(b => b).Select(r => r.Key));
        Assert.Contains(blocks.SelectMany(b => b), r => r.Values[Index("NOTIZ")] is LobValue); // LOBs as preview, like the grid
    }

    [Fact]
    public async Task A_whole_read_stops_when_the_consumer_has_enough_and_the_session_goes_on()
    {
        var blocks = 0;

        await _data.ReadAllAsync(_grid, [], [], (_, _, _) =>
        {
            blocks++;
            return Task.FromResult(false);
        }, Ct);

        Assert.Equal(1, blocks);
        Assert.Equal(RowCount, await CountAsync());
    }

    /// <summary>
    /// Review 3.44.0, C9: cancelling an export of all rows in the middle of its read – the cursor open, the token cancelled
    /// on the way – ends in a cancel and leaves the session usable, also for the names looked up meanwhile.
    /// The consumer's token is the session's own, cancelled on a pool thread (review 3.40.0, A3): it follows the caller's a
    /// moment later, so the consumer waits for it like a block being written – checking it at once raced the pool thread.
    /// </summary>
    [Fact]
    public async Task A_whole_read_cancelled_in_the_middle_leaves_the_session_usable()
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var blocks = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _data.ReadAllAsync(_grid, [], [], async (_, _, ct) =>
        {
            if (++blocks == 1)
            {
                await cancel.CancelAsync(); // the status bar's button
                await Task.Delay(TimeSpan.FromSeconds(30), ct); // ends with the cancel; if it never comes, a second block fails below
            }

            ct.ThrowIfCancellationRequested();
            return true;
        }, cancel.Token));

        Assert.Equal(1, blocks);
        Assert.Equal(RowCount, await CountAsync());
    }

    [Fact]
    public async Task Names_are_looked_up_in_the_same_session_while_the_read_is_open()
    {
        var found = new List<LookupRow>();
        var missingTable = _grid with { Table = new TableSummary(_grid.Table.Owner, "GIBT_ES_NICHT", TableKind.Table) };
        Core.Connections.DatabaseException? failed = null;

        await _data.ReadAllAsync(_grid, [], [], async (rows, lookup, ct) =>
        {
            found.AddRange(await lookup.LookupAsync(_grid, Index("ID"), [Index("GRUPPE")], [rows[0].Values[Index("ID")]!], ct));
            failed ??= await Assert.ThrowsAsync<Core.Connections.DatabaseException>(() =>
                lookup.LookupAsync(missingTable, Index("ID"), [Index("GRUPPE")], [1m], ct));
            return true; // the read goes on after a failed lookup
        }, Ct);

        Assert.Equal(3, found.Count); // one per block
        Assert.Equal(1m, found[0].Key);
        Assert.Equal([1m], found[0].Values);
        Assert.Equal("ORA-00942", failed!.ErrorCode);
    }

    [Fact]
    public async Task A_free_query_is_read_whole_and_refused_once_its_columns_changed()
    {
        var query = new QuerySpec("SELECT ID, NAME, NOTIZ FROM GRID_TEST ORDER BY ID", []);
        var rows = new List<RowData>();

        await _data.ReadSqlAllAsync(query, ["ID", "NAME", "NOTIZ"], (block, _, _) =>
        {
            rows.AddRange(block);
            return Task.FromResult(true);
        }, Ct);

        Assert.Equal(RowCount, rows.Count);
        Assert.Equal(Enumerable.Range(1, RowCount).Select(i => (object)(decimal)i), rows.Select(r => r.Values[0]));
        Assert.All(rows.Where(r => r.Values[2] is not null), r => Assert.IsType<LobValue>(r.Values[2]));
        await Assert.ThrowsAsync<Core.Connections.RefusedException>(() =>
            _data.ReadSqlAllAsync(query, ["ID", "NAME"], (_, _, _) => Task.FromResult(true), Ct));
    }

    // ---------- the values of a column (WP-57) ----------

    [Fact]
    public async Task The_values_of_a_column_come_in_the_order_of_the_grid()
    {
        IReadOnlyList<FilterCondition> filters = [FilterCondition.Of("GRUPPE", FilterOperator.NotEquals, "3")];
        IReadOnlyList<SortSpec> sorts = [new SortSpec("NAME", true)];
        var values = new List<object?>();

        await _data.ReadColumnAllAsync(_grid, [Index("ID")], filters, sorts, distinct: false, (rows, _, _) =>
        {
            Assert.All(rows, r => Assert.Single(r.Values));
            values.AddRange(rows.Select(r => r.Values[0]));
            return Task.FromResult(true);
        }, Ct);

        var paged = await ReadAllAsync(filters, sorts, pageSize: 500);
        Assert.Equal(paged.Select(r => r.Values[Index("ID")]), values);
    }

    [Fact]
    public async Task The_values_of_several_columns_come_side_by_side_in_the_order_of_the_grid()
    {
        IReadOnlyList<SortSpec> sorts = [new SortSpec("NAME", false)];
        var values = new List<IReadOnlyList<object?>>();

        await _data.ReadColumnAllAsync(_grid, [Index("NAME"), Index("ID")], [], sorts, distinct: false, (rows, _, _) =>
        {
            values.AddRange(rows.Select(r => r.Values));
            return Task.FromResult(true);
        }, Ct);

        var paged = await ReadAllAsync([], sorts, pageSize: 500);
        Assert.Equal(paged.Select(r => (r.Values[Index("NAME")], r.Values[Index("ID")])), values.Select(v => (v[0], v[1])));
    }

    [Fact]
    public async Task Distinct_values_of_a_column_come_each_once_with_null()
    {
        var values = new List<object?>();

        await _data.ReadColumnAllAsync(_grid, [Index("NAME")], [], [], distinct: true, (rows, _, _) =>
        {
            values.AddRange(rows.Select(r => r.Values[0]));
            return Task.FromResult(true);
        }, Ct);

        var all = (await ReadAllAsync([], [], pageSize: 500)).Select(r => r.Values[Index("NAME")]).ToList();
        Assert.Equal(all.Distinct().Count(), values.Count);
        Assert.Equal(all.Distinct().OfType<string>().Order(StringComparer.Ordinal), values.OfType<string>());
        Assert.Contains(null, values);
    }

    [Fact]
    public async Task The_streaming_reader_runs_plain_queries_only()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => _session!.ExecuteStreamingReaderAsync(
            "DELETE FROM GRID_TEST", [], (_, _, _) => Task.FromResult(true), OracleSession.WholeLobs, Ct));

        Assert.Equal(RowCount, await CountAsync());
    }
}
