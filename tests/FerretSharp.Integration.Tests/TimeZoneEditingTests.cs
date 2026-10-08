using FerretSharp.Core.Connections;
using FerretSharp.Core.Data;
using FerretSharp.Core.Oracle;
using FerretSharp.Core.Query;
using FerretSharp.Core.Schema;
using Oracle.ManagedDataAccess.Client;

namespace FerretSharp.Integration.Tests;

/// <summary>
/// Editing TIMESTAMP WITH TIME ZONE and WITH LOCAL TIME ZONE (3.12.0; excluded in WP-09): values keep their offset, a
/// local time zone value round-trips in the session's time zone, values stored with a region name pass the concurrency
/// check.
/// </summary>
public sealed class TimeZoneEditingTests(OracleContainerFixture oracle) : IAsyncLifetime
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static bool _created;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Owner => oracle.RequireProfile().Profile.EffectiveSchema;

    public async ValueTask InitializeAsync()
    {
        await Gate.WaitAsync(Ct);
        try
        {
            if (_created)
            {
                return;
            }

            foreach (var sql in new[]
            {
                """
                CREATE TABLE ED_ZEITEN (
                    ID  NUMBER(10) PRIMARY KEY,
                    TZ  TIMESTAMP(6) WITH TIME ZONE,
                    LTZ TIMESTAMP(6) WITH LOCAL TIME ZONE) SEGMENT CREATION IMMEDIATE
                """,
                "INSERT INTO ED_ZEITEN VALUES (1, TIMESTAMP '2026-10-08 12:00:00.5 +02:00', TIMESTAMP '2026-10-08 10:00:00 +00:00')",
                "INSERT INTO ED_ZEITEN VALUES (2, TIMESTAMP '2026-07-01 09:30:00 Europe/Berlin', NULL)",
            })
            {
                await ExecuteAsync(sql);
            }

            _created = true;
        }
        finally
        {
            Gate.Release();
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new OracleConnection(oracle.RequireConnectionString());
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(Ct);
    }

    private async Task<string> ScalarAsync(string sql)
    {
        await using var connection = new OracleConnection(oracle.RequireConnectionString());
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(await command.ExecuteScalarAsync(Ct), System.Globalization.CultureInfo.InvariantCulture)!;
    }

    private async Task<IDatabaseConnection> OpenAsync(string action)
    {
        var (profile, password) = oracle.RequireProfile();
        return await new OracleDatabaseConnector().OpenAsync(profile, password, action, Ct);
    }

    private static async Task<RowData> RowAsync(IDatabaseConnection connection, TableDetails table, int id) =>
        (await connection.Data.ReadPageAsync(table, [FilterCondition.Of("ID", FilterOperator.Equals, id.ToString())], [], new PageSpec(0, 10), Ct))
        .Rows.Single();

    private static int I(TableDetails table, string column) => table.Columns.ToList().FindIndex(c => c.Name == column);

    private static async Task FlushAndCommitAsync(IDatabaseConnection connection, ChangeTracker tracker)
    {
        var operations = tracker.PendingOperations();
        var result = await connection.Editor.FlushAsync(tracker.Table, operations, new FlushOptions(), Ct);
        tracker.MarkFlushed(operations, result.NewKeys);
        await connection.Editor.CommitAsync(Ct);
        tracker.Clear();
    }

    [Fact]
    public async Task Values_with_time_zone_are_read_written_and_kept_with_their_offset()
    {
        await using var a = await OpenAsync("TZ edit");
        await using var check = await OpenAsync("TZ check");
        var table = await a.Schema.GetDetailsAsync(new TableSummary(Owner, "ED_ZEITEN", TableKind.Table), Ct);
        var tz = I(table, "TZ");
        var ltz = I(table, "LTZ");

        var row = await RowAsync(a, table, 1);
        Assert.Equal(new DateTimeOffset(2026, 10, 8, 12, 0, 0, 500, TimeSpan.FromHours(2)), row.Values[tz]);
        Assert.IsType<DateTime>(row.Values[ltz]); // in the session's time zone, without offset
        Assert.Null(OracleTypeMapper.NotEditableReason(table, table.Columns[tz], newRow: false));
        Assert.Null(OracleTypeMapper.NotEditableReason(table, table.Columns[ltz], newRow: false));

        var tracker = new ChangeTracker(table);
        Assert.True(tracker.SetValue(row, tz, "09.10.2026 08:15:00,25 -05:00").IsValid);
        Assert.True(tracker.SetValue(row, ltz, "09.10.2026 08:15:00").IsValid);
        await FlushAndCommitAsync(a, tracker);

        var stored = await RowAsync(check, table, 1);
        Assert.Equal(new DateTimeOffset(2026, 10, 9, 8, 15, 0, 250, TimeSpan.FromHours(-5)), stored.Values[tz]);
        Assert.Equal(new DateTime(2026, 10, 9, 8, 15, 0), stored.Values[ltz]); // same session time zone: the same time
        Assert.Equal("2026-10-09 08:15:00.250000 -05:00",
            await ScalarAsync("SELECT TO_CHAR(TZ, 'YYYY-MM-DD HH24:MI:SS.FF6 TZH:TZM') FROM ED_ZEITEN WHERE ID = 1"));

        // The same instant with another offset is a change: the offset is part of the value.
        Assert.True(tracker.SetValue(stored, tz, "09.10.2026 13:15:00,25 +00:00").IsValid);
        Assert.Equal(1, tracker.PendingCount);
        await FlushAndCommitAsync(a, tracker);
        Assert.Equal("+00:00", await ScalarAsync("SELECT TO_CHAR(TZ, 'TZH:TZM') FROM ED_ZEITEN WHERE ID = 1"));
    }

    [Fact]
    public async Task A_value_stored_with_a_region_passes_the_concurrency_check()
    {
        await using var a = await OpenAsync("TZ region");
        var table = await a.Schema.GetDetailsAsync(new TableSummary(Owner, "ED_ZEITEN", TableKind.Table), Ct);
        var row = await RowAsync(a, table, 2);
        Assert.Equal(new DateTimeOffset(2026, 7, 1, 9, 30, 0, TimeSpan.FromHours(2)), row.Values[I(table, "TZ")]); // Europe/Berlin, summer

        // Writing another column checks nothing about TZ; writing TZ compares the read value with the locked row.
        var tracker = new ChangeTracker(table);
        Assert.True(tracker.SetValue(row, I(table, "TZ"), "01.07.2026 10:00 +02:00").IsValid);
        await FlushAndCommitAsync(a, tracker);
        Assert.Equal("2026-07-01 10:00:00 +02:00",
            await ScalarAsync("SELECT TO_CHAR(TZ, 'YYYY-MM-DD HH24:MI:SS TZH:TZM') FROM ED_ZEITEN WHERE ID = 2"));
    }

    [Fact]
    public async Task New_rows_take_values_with_time_zone()
    {
        await using var a = await OpenAsync("TZ insert");
        var table = await a.Schema.GetDetailsAsync(new TableSummary(Owner, "ED_ZEITEN", TableKind.Table), Ct);
        var tracker = new ChangeTracker(table);
        var added = tracker.AddRow();
        tracker.SetValue(added, I(table, "ID"), "3");
        tracker.SetValue(added, I(table, "TZ"), "2026-12-24T18:00:00+01:00");
        tracker.SetValue(added, I(table, "LTZ"), "24.12.2026 18:00");
        await FlushAndCommitAsync(a, tracker);

        var stored = await RowAsync(a, table, 3);
        Assert.Equal(new DateTimeOffset(2026, 12, 24, 18, 0, 0, TimeSpan.FromHours(1)), stored.Values[I(table, "TZ")]);
        Assert.Equal(new DateTime(2026, 12, 24, 18, 0, 0), stored.Values[I(table, "LTZ")]);
    }
}
