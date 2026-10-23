using FerretSharp.Core.Connections;
using FerretSharp.Core.Data;
using FerretSharp.Core.Oracle;
using FerretSharp.Core.Query;
using Oracle.ManagedDataAccess.Client;

namespace FerretSharp.Integration.Tests;

/// <summary>
/// A query result sorted by a header click, against Oracle: each form of <see cref="SqlResultOrder"/> runs (appended, in
/// front of the query's own ORDER BY, wrapped around FETCH FIRST, with columns of the same name), and the rows sorted in
/// memory (<see cref="RowOrder"/>) come in the order Oracle gives them in a FerretSharp session.
/// </summary>
public sealed class SqlResultOrderTests(OracleContainerFixture oracle) : IAsyncLifetime
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static bool _created;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await Gate.WaitAsync(Ct);
        try
        {
            if (_created)
            {
                return;
            }

            await using var connection = new OracleConnection(oracle.RequireConnectionString());
            await connection.OpenAsync(Ct);
            foreach (var sql in new[]
            {
                "CREATE TABLE SO_WERT (ID NUMBER(10) PRIMARY KEY, TEXT VARCHAR2(20 CHAR), NTEXT NVARCHAR2(20), ZAHL NUMBER, BIN BINARY_DOUBLE, DATUM DATE, ZEIT TIMESTAMP(6) WITH TIME ZONE, ROH RAW(4), GRUPPE VARCHAR2(1))",
                "INSERT INTO SO_WERT VALUES (1, 'zebra', N'Łódź', 12345678901234567890123456789012345678, 2.5, DATE '2026-10-23', TIMESTAMP '2026-10-23 12:00:00 +02:00', HEXTORAW('FF'), 'B')",
                "INSERT INTO SO_WERT VALUES (2, 'Äpfel', N'apfel', 0.00000000000000000000000000000001, BINARY_DOUBLE_NAN, DATE '2026-01-01', TIMESTAMP '2026-10-23 11:00:00 +00:00', HEXTORAW('0100'), 'A')",
                "INSERT INTO SO_WERT VALUES (3, 'apfel', N'Zander', -5, BINARY_DOUBLE_INFINITY, NULL, TIMESTAMP '2026-10-23 10:30:00 +00:00', HEXTORAW('01'), 'B')",
                "INSERT INTO SO_WERT VALUES (4, 'Zander', NULL, 1.5, -1, DATE '2025-12-31', NULL, NULL, 'A')",
                "INSERT INTO SO_WERT VALUES (5, '10', N'10', NULL, -BINARY_DOUBLE_INFINITY, DATE '2026-06-15', TIMESTAMP '2026-10-23 09:00:00 -05:00', HEXTORAW('0001'), 'B')",
                "INSERT INTO SO_WERT VALUES (6, '9', N'9', 2, NULL, DATE '2026-06-15', TIMESTAMP '2026-10-23 09:00:00 +01:00', HEXTORAW('00'), 'A')",
                "INSERT INTO SO_WERT VALUES (7, NULL, N'Äpfel', 12345678901234567890123456789012345679, 0, DATE '2026-10-23', TIMESTAMP '2026-10-23 08:00:00 +00:00', HEXTORAW('FF00'), 'B')",
                "COMMIT",
            })
            {
                await using var command = connection.CreateCommand();
                command.CommandText = sql;
                await command.ExecuteNonQueryAsync(Ct);
            }

            _created = true;
        }
        finally
        {
            Gate.Release();
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private async Task<IDatabaseConnection> OpenAsync()
    {
        var (profile, password) = oracle.RequireProfile();
        return await new OracleDatabaseConnector().OpenAsync(profile, password, "SqlResultOrderTests", Ct);
    }

    private static async Task<SqlPage> ReadAsync(IDatabaseConnection connection, QuerySpec query) =>
        await connection.Data.ReadSqlAsync(query, 0, 100, Ct);

    private static IReadOnlyList<object?> Ids(IEnumerable<IReadOnlyList<object?>> rows) => [.. rows.Select(r => r[0])];

    /// <summary>Every column, both directions: the database's order and the one in memory are the same.</summary>
    [Fact]
    public async Task Memory_sorts_like_the_database()
    {
        await using var connection = await OpenAsync();
        var query = new QuerySpec("SELECT id, text, ntext, zahl, bin, datum, zeit, roh FROM so_wert ORDER BY id", []);
        var page = await ReadAsync(connection, query);
        var loaded = page.Rows.Select(values => new RowData(RowKey.None.Instance, values)).ToList();

        for (var column = 1; column < page.Columns.Count; column++)
        {
            Assert.True(RowOrder.CanSort(page.Columns[column].Column), page.Columns[column].Name);
            foreach (var descending in new[] { false, true })
            {
                ResultSort[] sorts = [new(column, descending), new(0, false)]; // ties (2026-06-15) by id on both sides
                var byDatabase = await ReadAsync(connection, SqlResultOrder.Sorted(query, page.Columns, sorts)!);

                Assert.Equal(Ids(byDatabase.Rows), Ids(RowOrder.Sort(loaded, sorts).Select(r => r.Values)));
            }
        }
    }

    [Fact]
    public async Task The_query_s_own_order_breaks_ties()
    {
        await using var connection = await OpenAsync();
        var query = new QuerySpec("SELECT gruppe, id FROM so_wert ORDER BY id DESC", []);
        var page = await ReadAsync(connection, query);

        var sorted = await ReadAsync(connection, SqlResultOrder.Sorted(query, page.Columns, [new ResultSort(0, false)])!);

        Assert.Equal([6m, 4m, 2m, 7m, 5m, 3m, 1m], sorted.Rows.Select(r => r[1]));
    }

    [Fact]
    public async Task Columns_of_the_same_name_sort_in_the_query()
    {
        await using var connection = await OpenAsync();
        var query = new QuerySpec("SELECT a.id, b.id, a.text FROM so_wert a JOIN so_wert b ON b.id = 8 - a.id WHERE a.id > :von", [new QueryParameter("von", 2, OracleTypeHint.Number)]);
        var page = await ReadAsync(connection, query);

        var sorted = await ReadAsync(connection, SqlResultOrder.Sorted(query, page.Columns, [new ResultSort(1, false)])!);

        Assert.Equal(["ID", "ID", "TEXT"], page.Columns.Select(c => c.Name));
        Assert.Equal([1m, 2m, 3m, 4m, 5m], sorted.Rows.Select(r => r[1]));
    }

    [Fact]
    public async Task A_limited_query_is_wrapped_and_keeps_its_rows()
    {
        await using var connection = await OpenAsync();
        var query = new QuerySpec("WITH w AS (SELECT * FROM so_wert) SELECT id, zahl FROM w ORDER BY id DESC FETCH FIRST 3 ROWS ONLY", []);
        var page = await ReadAsync(connection, query);

        var sorted = await ReadAsync(connection, SqlResultOrder.Sorted(query, page.Columns, [new ResultSort(1, true)])!);

        Assert.Equal([7m, 6m, 5m], page.Rows.Select(r => r[0]));
        Assert.Equal([5m, 7m, 6m], sorted.Rows.Select(r => r[0])); // NULL first descending, then the larger number
    }
}
