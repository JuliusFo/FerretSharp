using FerretSharp.Core.Connections;
using FerretSharp.Core.Data;
using FerretSharp.Core.Oracle;
using FerretSharp.Core.Query;
using FerretSharp.Core.Schema;
using Oracle.ManagedDataAccess.Client;

namespace FerretSharp.Integration.Tests;

/// <summary>
/// Plans (ADR 0012): estimated with EXPLAIN PLAN on a session without transaction (never in a locked one), actual from
/// the cursor of a run with GATHER_PLAN_STATISTICS (needs V$ rights; without them a clear message).
/// </summary>
public sealed class PlanTests(OracleContainerFixture oracle) : IAsyncLifetime
{
    private const string NoRightsUser = "FS_PLAN_NORIGHTS";
    private const string NoRightsPassword = "Plan2026x";
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

            await ExecuteAsync(oracle.RequireConnectionString(),
                "CREATE TABLE PL_T (ID NUMBER(10) PRIMARY KEY, N VARCHAR2(20))",
                "INSERT INTO PL_T SELECT LEVEL, 'x' || LEVEL FROM DUAL CONNECT BY LEVEL <= 1200",
                "COMMIT",
                "BEGIN DBMS_STATS.GATHER_TABLE_STATS(USER, 'PL_T'); END;");
            await ExecuteAsync(System(),
                $"GRANT SELECT_CATALOG_ROLE TO {OracleIdentifier.Quote(Owner)}",
                $"CREATE USER {NoRightsUser} IDENTIFIED BY {NoRightsPassword}",
                $"GRANT CREATE SESSION TO {NoRightsUser}");
            _created = true;
        }
        finally
        {
            Gate.Release();
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private string System() => new OracleConnectionStringBuilder(oracle.RequireConnectionString()) { UserID = "system" }.ConnectionString;

    private static async Task ExecuteAsync(string connectionString, params string[] statements)
    {
        await using var connection = new OracleConnection(connectionString);
        await connection.OpenAsync(Ct);
        foreach (var sql in statements)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync(Ct);
        }
    }

    private async Task<IDatabaseConnection> OpenAsync(string action, string? user = null, string? password = null)
    {
        var (profile, defaultPassword) = oracle.RequireProfile();
        if (user is not null)
        {
            profile = profile with { User = user };
        }

        return await new OracleDatabaseConnector().OpenAsync(profile, password ?? defaultPassword, action, Ct);
    }

    /// <summary>The grid's query of the first page with a filter (binds :p0, :p_offset, :p_limit).</summary>
    private async Task<QuerySpec> GridQueryAsync(IDatabaseConnection connection)
    {
        var table = await connection.Schema.GetDetailsAsync(new TableSummary(Owner, "PL_T", TableKind.Table), Ct);
        var select = QueryBuilder.BuildSelect(table, [FilterCondition.Of("ID", FilterOperator.Gt, "10")], [], new PageSpec(0, IDataAccess.ActualPlanPageSize));
        return new QuerySpec(select.Sql, select.Parameters);
    }

    [Fact]
    public async Task The_estimated_plan_needs_no_bind_values_and_leaves_nothing_behind()
    {
        await using var connection = await OpenAsync("Plan estimated");
        var query = await GridQueryAsync(connection);

        var plan = await connection.Schema.ExplainAsync(query, Ct);

        Assert.Equal(PlanSource.Estimated, plan.Source);
        Assert.Equal("SELECT STATEMENT", plan.Steps[0].Operation);
        Assert.Contains(plan.Steps, s => s.ObjectName == "PL_T");
        Assert.All(plan.Steps.Skip(1), s => Assert.NotNull(s.ParentId));
        Assert.Null(plan.Steps[0].ActualRows);
        Assert.Equal(query.Sql, plan.Sql);
        // The PLAN_TABLE rows are gone again.
        var left = await connection.Data.ReadSqlAsync(new QuerySpec("SELECT COUNT(*) FROM PLAN_TABLE", []), 0, 1, Ct);
        Assert.Equal(0m, left.Rows[0][0]);
    }

    [Fact]
    public async Task A_locked_session_cannot_explain_and_says_so()
    {
        await using var connection = await OpenAsync("Plan locked");
        var query = await GridQueryAsync(connection);
        await connection.UseReadOnlySnapshotsAsync(Ct);

        var error = await Assert.ThrowsAsync<PlanUnavailableException>(() => connection.Schema.ExplainAsync(query, Ct));

        Assert.Contains("READ ONLY", error.Message);
    }

    [Fact]
    public async Task The_actual_plan_measures_the_first_page_or_the_whole_result()
    {
        await using var connection = await OpenAsync("Plan actual");
        var all = new QuerySpec("SELECT \"ID\", \"N\" FROM \"PL_T\" WHERE \"ID\" > :p0", [new QueryParameter("p0", 10)]);

        var firstPage = await connection.Data.ExplainActualAsync(all, wholeResult: false, Ct);
        var whole = await connection.Data.ExplainActualAsync(all, wholeResult: true, Ct);

        Assert.Equal(PlanSource.Actual, firstPage.Source);
        Assert.StartsWith("SELECT /*+ GATHER_PLAN_STATISTICS */", firstPage.Sql);
        Assert.Equal(Plans.SqlId(firstPage.Sql), firstPage.SqlId);
        Assert.Equal(IDataAccess.ActualPlanPageSize, firstPage.RowsFetched);
        Assert.Equal(1190, whole.RowsFetched);
        var scan = Assert.Single(whole.Steps, s => s.ObjectName == "PL_T");
        Assert.Equal(1190, scan.ActualRows);
        Assert.Equal(1, scan.Starts);
        Assert.NotNull(scan.BufferGets);
        Assert.NotNull(whole.Steps[0].ActualTime);
        Assert.Contains("A-Rows", Plans.Format(whole));
    }

    [Fact]
    public async Task Without_rights_on_v_dollar_views_the_actual_plan_names_the_grant()
    {
        await using var connection = await OpenAsync("Plan no rights", NoRightsUser, NoRightsPassword);

        var error = await Assert.ThrowsAsync<PlanUnavailableException>(() =>
            connection.Data.ExplainActualAsync(new QuerySpec("SELECT * FROM DUAL", []), wholeResult: false, Ct));

        Assert.Contains("SELECT_CATALOG_ROLE", error.Message);
    }
}
