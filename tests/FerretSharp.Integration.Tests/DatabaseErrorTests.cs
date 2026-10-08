using System.Globalization;
using FerretSharp.Core.Connections;
using FerretSharp.Core.Oracle;
using FerretSharp.Core.Query;
using FerretSharp.Core.Schema;
using Oracle.ManagedDataAccess.Client;

namespace FerretSharp.Integration.Tests;

/// <summary>Errors as the UI sees them: with the failing statement, and a killed session recognized as connection loss.</summary>
public sealed class DatabaseErrorTests(OracleContainerFixture oracle)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly TableDetails Missing = new(
        new TableSummary("NOBODY", "GIBT_ES_NICHT", TableKind.Table),
        [new ColumnInfo("ID", "NUMBER", null, false, 10, 0, false, false, null, 1)],
        ["ID"], [], false);

    private async Task<IDatabaseConnection> OpenAsync(string action)
    {
        var (profile, password) = oracle.RequireProfile();
        return await new OracleDatabaseConnector().OpenAsync(profile, password, action, Ct);
    }

    [Fact]
    public async Task Failed_statement_carries_sql_and_binds()
    {
        await using var connection = await OpenAsync("Error test");

        var error = await Assert.ThrowsAsync<DatabaseException>(
            () => connection.Data.CountAsync(Missing, [FilterCondition.Of("ID", FilterOperator.Equals, "4711")], Ct));

        Assert.Equal("ORA-00942", error.ErrorCode);
        Assert.False(error.IsConnectionLost);
        Assert.Contains("\"NOBODY\".\"GIBT_ES_NICHT\"", error.Statement!.Sql);
        Assert.Equal(4711m, Assert.Single(error.Statement.Parameters).Value);
    }

    [Fact]
    public async Task Killed_session_is_reported_as_connection_lost_also_on_later_queries()
    {
        var action = "Kill test " + Guid.NewGuid().ToString("N")[..8];
        await using var connection = await OpenAsync(action);
        await SampleSchema.EnsureCreatedAsync(oracle.RequireConnectionString(), Ct);
        var (profile, _) = oracle.RequireProfile();
        var grid = await connection.Schema.GetDetailsAsync(new TableSummary(profile.EffectiveSchema, "GRID_TEST", TableKind.Table), Ct);

        await KillAsync(action);

        var first = await Assert.ThrowsAsync<DatabaseException>(() => connection.Data.CountAsync(grid, [], Ct));
        var second = await Assert.ThrowsAsync<DatabaseException>(() => connection.Data.CountAsync(grid, [], Ct));

        Assert.True(first.IsConnectionLost, first.Display);
        Assert.True(second.IsConnectionLost, second.Display);
        Assert.NotNull(second.Statement);
    }

    /// <summary>
    /// A locked session restarts its snapshot on the first page; after a kill that must be reported as connection
    /// loss – and the next read must not fall back to autocommit (no snapshot, no ORA-01456) but fail the same way.
    /// </summary>
    [Fact]
    public async Task Killed_locked_session_is_reported_as_connection_lost_and_stays_locked()
    {
        var action = "Kill locked " + Guid.NewGuid().ToString("N")[..8];
        await using var connection = await OpenAsync(action);
        await SampleSchema.EnsureCreatedAsync(oracle.RequireConnectionString(), Ct);
        var (profile, _) = oracle.RequireProfile();
        var grid = await connection.Schema.GetDetailsAsync(new TableSummary(profile.EffectiveSchema, "GRID_TEST", TableKind.Table), Ct);
        await connection.UseReadOnlySnapshotsAsync(Ct);

        await KillAsync(action);

        var first = await Assert.ThrowsAsync<DatabaseException>(() => connection.Data.ReadPageAsync(grid, [], [], new PageSpec(0, 10), Ct));
        var second = await Assert.ThrowsAsync<DatabaseException>(() => connection.Data.ReadPageAsync(grid, [], [], new PageSpec(10, 10), Ct));

        Assert.True(first.IsConnectionLost, first.Display);
        Assert.True(second.IsConnectionLost, second.Display);
        Assert.True(connection.UsesReadOnlySnapshots);
    }

    /// <summary>Closing a workspace while its grid reads: the statement is cancelled, the session closes promptly.</summary>
    [Fact]
    public async Task Disposing_cancels_a_running_query_and_refuses_later_calls()
    {
        var connection = await OpenAsync("Dispose test");
        var slow = new QuerySpec("SELECT COUNT(*) FROM ALL_OBJECTS a CROSS JOIN ALL_OBJECTS b CROSS JOIN ALL_OBJECTS c", []);
        var running = connection.Data.ReadSqlAsync(slow, 0, 1, Ct);
        await Task.Delay(1000, Ct);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await connection.DisposeAsync();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(9), $"Dispose took {stopwatch.Elapsed}");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connection.Data.ReadSqlAsync(new QuerySpec("SELECT 1 FROM DUAL", []), 0, 1, Ct));
        await connection.DisposeAsync(); // twice is fine
    }

    [Fact]
    public async Task Keep_alive_ping_skips_a_recently_used_session_and_finds_a_killed_one()
    {
        var action = "Ping test " + Guid.NewGuid().ToString("N")[..8];
        await using var connection = await OpenAsync(action);

        Assert.False(await connection.PingIfIdleAsync(TimeSpan.FromMinutes(1), Ct)); // just opened
        Assert.True(await connection.PingIfIdleAsync(TimeSpan.Zero, Ct));

        await KillAsync(action);

        var error = await Assert.ThrowsAsync<DatabaseException>(() => connection.PingIfIdleAsync(TimeSpan.Zero, Ct));
        Assert.True(error.IsConnectionLost, error.Display);
    }

    /// <summary>As SYSTEM (same password as the app user in the gvenzl image).</summary>
    private async Task KillAsync(string action)
    {
        var system = new OracleConnectionStringBuilder(oracle.RequireConnectionString()) { UserID = "system" };
        await using var connection = new OracleConnection(system.ConnectionString);
        try
        {
            await connection.OpenAsync(Ct);
        }
        catch (OracleException ex)
        {
            Assert.Skip($"SYSTEM login not possible: {ex.Message}");
        }

        await using var find = connection.CreateCommand();
        find.CommandText = "SELECT SID || ',' || SERIAL# FROM V$SESSION WHERE ACTION = :a";
        find.Parameters.Add(new OracleParameter("a", action));
        var session = (string)(await find.ExecuteScalarAsync(Ct))!;

        await using var kill = connection.CreateCommand();
        kill.CommandText = $"ALTER SYSTEM KILL SESSION '{session}' IMMEDIATE";
        try
        {
            await kill.ExecuteNonQueryAsync(Ct);
        }
        catch (OracleException ex) when (ex.Number == 31)
        {
            // ORA-00031 "session marked for kill": Oracle could not end it at once (a loaded test container) – wait until it is gone.
            await using var gone = connection.CreateCommand();
            gone.CommandText = "SELECT COUNT(*) FROM V$SESSION WHERE ACTION = :a AND STATUS <> 'KILLED'";
            gone.Parameters.Add(new OracleParameter("a", action));
            for (var waited = 0; Convert.ToInt32(await gone.ExecuteScalarAsync(Ct), CultureInfo.InvariantCulture) > 0 && waited < 30; waited++)
            {
                await Task.Delay(1000, Ct);
            }
        }
    }
}
