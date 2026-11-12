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
    private const string HeapPlanUser = "FS_PLAN_HEAP";
    private const string HeapPlanPassword = "Plan2026h";
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
                $"GRANT CREATE SESSION TO {NoRightsUser}",
                $"CREATE USER {HeapPlanUser} IDENTIFIED BY {HeapPlanPassword} QUOTA UNLIMITED ON USERS",
                $"GRANT CREATE SESSION, CREATE TABLE, CREATE TRIGGER TO {HeapPlanUser}");
            // An old PLAN_TABLE of the schema (utlxplan.sql): a heap table that hides the public synonym. The trigger notes
            // every row written into it, committed or not – without a transaction, ODP.NET commits each statement (A37).
            await ExecuteAsync(User(HeapPlanUser, HeapPlanPassword),
                """
                CREATE TABLE PLAN_TABLE (
                  statement_id VARCHAR2(30), plan_id NUMBER, timestamp DATE, remarks VARCHAR2(4000), operation VARCHAR2(30),
                  options VARCHAR2(255), object_node VARCHAR2(128), object_owner VARCHAR2(128), object_name VARCHAR2(128),
                  object_alias VARCHAR2(261), object_instance NUMBER, object_type VARCHAR2(30), optimizer VARCHAR2(255),
                  search_columns NUMBER, id NUMBER, parent_id NUMBER, depth NUMBER, position NUMBER, cost NUMBER,
                  cardinality NUMBER, bytes NUMBER, other_tag VARCHAR2(255), partition_start VARCHAR2(255),
                  partition_stop VARCHAR2(255), partition_id NUMBER, other LONG, distribution VARCHAR2(30), cpu_cost NUMBER,
                  io_cost NUMBER, temp_space NUMBER, access_predicates VARCHAR2(4000), filter_predicates VARCHAR2(4000),
                  projection VARCHAR2(4000), time NUMBER, qblock_name VARCHAR2(128), other_xml CLOB)
                """,
                "CREATE TABLE PLAN_WRITES (N NUMBER)",
                "CREATE TRIGGER PLAN_WRITES_T AFTER INSERT ON PLAN_TABLE FOR EACH ROW BEGIN INSERT INTO PLAN_WRITES VALUES (1); END;");
            _created = true;
        }
        finally
        {
            Gate.Release();
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private string System() => new OracleConnectionStringBuilder(oracle.RequireConnectionString()) { UserID = "system" }.ConnectionString;

    private string User(string user, string password) =>
        new OracleConnectionStringBuilder(oracle.RequireConnectionString()) { UserID = user, Password = password }.ConnectionString;

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
        Assert.All(plan.Steps.Skip(1), s => Assert.True(s.Depth > 0));
        Assert.Null(plan.Steps[0].ActualRows);
        Assert.Equal(query.Sql, plan.Sql);
        // The PLAN_TABLE rows are gone again.
        var left = await connection.Data.ReadSqlAsync(new QuerySpec("SELECT COUNT(*) FROM SYS.PLAN_TABLE$", []), 0, 1, Ct);
        Assert.Equal(0m, left.Rows[0][0]);
    }

    [Fact]
    public async Task The_estimated_plan_never_writes_into_a_plan_table_of_the_schema()
    {
        await using var connection = await OpenAsync("Plan heap table", HeapPlanUser, HeapPlanPassword);

        var plan = await connection.Schema.ExplainAsync(new QuerySpec("SELECT * FROM DUAL WHERE DUMMY = :p0", []), Ct);

        Assert.Contains(plan.Steps, s => s.ObjectName == "DUAL");
        // Counted from another session: what the explain wrote and committed into the schema's own table.
        await using var other = new OracleConnection(User(HeapPlanUser, HeapPlanPassword));
        await other.OpenAsync(Ct);
        await using var count = other.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM PLAN_WRITES";
        Assert.Equal(0m, (decimal)(await count.ExecuteScalarAsync(Ct))!);
        // A table of the query that does not exist stays the query's error, not a missing right on the plan table.
        var missing = await Assert.ThrowsAsync<DatabaseException>(() => connection.Schema.ExplainAsync(new QuerySpec("SELECT * FROM NO_SUCH_TABLE", []), Ct));
        Assert.IsNotType<PlanUnavailableException>(missing);
        Assert.Equal("ORA-00942", missing.ErrorCode);
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
    public async Task A_statement_another_program_ran_is_found_by_its_text()
    {
        // Like EF: a statement over several lines with a bind, run by another session (the traced app, WP-33).
        var sql = $"SELECT \"ID\", \"N\"\r\nFROM \"PL_T\" /* {Guid.NewGuid():N} */\r\nWHERE \"ID\" <= :p0";
        async Task RunAsync(int times)
        {
            await using var app = new OracleConnection(oracle.RequireConnectionString());
            await app.OpenAsync(Ct);
            for (var i = 0; i < times; i++)
            {
                await using var command = app.CreateCommand();
                command.CommandText = sql;
                command.Parameters.Add(new OracleParameter("p0", 5));
                await using var reader = await command.ExecuteReaderAsync(Ct);
                while (await reader.ReadAsync(Ct))
                {
                }
            }
        }

        await RunAsync(3);
        await using var connection = await OpenAsync("Cursor lookup");
        var cursor = await connection.Schema.GetCursorAsync(sql, Ct);

        Assert.NotNull(cursor);
        Assert.Equal(Plans.SqlId(sql), cursor.SqlId);
        Assert.Equal(3, cursor.Executions);
        Assert.Equal(15, cursor.RowsProcessed);
        Assert.True(cursor.Children >= 1);
        Assert.NotNull(cursor.LastActive);
        Assert.Equal(PlanSource.Actual, cursor.Plan.Source);
        Assert.Equal(sql, cursor.Plan.Sql);
        // Oracle may have dropped the plan meanwhile (fix-cursor-plan-child): then the next run brings it back.
        var plan = await CursorPlans.ReadAsync(() => connection.Schema.GetCursorAsync(sql, Ct), () => RunAsync(1));
        Assert.Contains(plan.Steps, s => s.ObjectName == "PL_T");
        Assert.Null(await connection.Schema.GetCursorAsync(sql + " ", Ct)); // another text, another SQL_ID
    }

    [Fact]
    public async Task Without_rights_on_v_dollar_views_the_cursor_lookup_names_the_grant()
    {
        await using var connection = await OpenAsync("Cursor no rights", NoRightsUser, NoRightsPassword);

        var error = await Assert.ThrowsAsync<PlanUnavailableException>(() => connection.Schema.GetCursorAsync("SELECT * FROM DUAL", Ct));

        Assert.Contains("SELECT_CATALOG_ROLE", error.Message);
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
