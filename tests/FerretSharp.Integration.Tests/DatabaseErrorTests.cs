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
        await kill.ExecuteNonQueryAsync(Ct);
    }
}
