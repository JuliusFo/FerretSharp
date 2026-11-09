using FerretSharp.Core.Connections;
using FerretSharp.Core.Oracle;
using FerretSharp.Core.Query;
using FerretSharp.Core.ServerLoad;
using FerretSharp.Testing;
using Oracle.ManagedDataAccess.Client;

namespace FerretSharp.Integration.Tests;

/// <summary>
/// The server load (WP-75, ADR 0045) against a real database: every statement of the reader runs, a statement another
/// session runs shows up with its figures and text, a waiting session is listed with what it waits for, and without rights
/// the reader says what is missing.
/// </summary>
public sealed class ServerLoadTests(OracleContainerFixture oracle) : IAsyncLifetime
{
    private const string NoRightsUser = "FS_LOAD_NORIGHTS";
    private const string NoRightsPassword = "Load2026x";
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

    /// <summary>
    /// The tracker's count of a statement next to what V$SQLSTATS and V$SQL hold for it right now (executions, last active,
    /// plan, container) – only for the failure message.
    /// </summary>
    private async Task<string> CursorStateAsync(string sqlId, ServerLoadTracker tracker, string label)
    {
        var counted = tracker.Top(null, TopStatementSort.Executions, StatementFilter.None, 10_000).SingleOrDefault(r => r.SqlId == sqlId)?.Figures.Executions;
        await using var connection = new OracleConnection(System());
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.BindByName = true;
        command.CommandText = """
            SELECT TO_CHAR(SYSTIMESTAMP, 'HH24:MI:SS.FF3'),
                   (SELECT LISTAGG(executions || '@' || TO_CHAR(last_active_time, 'HH24:MI:SS') || ' p' || plan_hash_value || ' c' || con_id, '; ')
                      FROM v$sqlstats WHERE sql_id = :sql_id),
                   (SELECT LISTAGG(child_number || ':' || executions || '@' || TO_CHAR(last_active_time, 'HH24:MI:SS') || ' c' || con_id, '; ')
                      FROM v$sql WHERE sql_id = :sql_id)
              FROM dual
            """;
        command.Parameters.Add(new OracleParameter("sql_id", sqlId));
        await using var reader = await command.ExecuteReaderAsync(Ct);
        await reader.ReadAsync(Ct);
        return $"  {label}: counted {counted?.ToString() ?? "-"} | now {reader.GetString(0)} | V$SQLSTATS {(reader.IsDBNull(1) ? "-" : reader.GetString(1))}"
            + $" | V$SQL {(reader.IsDBNull(2) ? "-" : reader.GetString(2))}";
    }

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

    private async Task<IDatabaseConnection> OpenAsync(string? user = null, string? password = null)
    {
        var (profile, defaultPassword) = oracle.RequireProfile();
        if (user is not null)
        {
            profile = profile with { User = user };
        }

        return await new OracleDatabaseConnector().OpenAsync(profile, password ?? defaultPassword, "Serverlast", Ct);
    }

    [Fact]
    public async Task A_statement_another_session_runs_shows_up_with_its_figures_text_and_plan()
    {
        await using var connection = await OpenAsync();
        var monitor = new ServerLoadMonitor(connection.ServerLoad, new ServerLoadTracker());
        await monitor.OpenAsync(null, Ct);
        Assert.Equal(1, monitor.Environment!.Instances);
        Assert.True(monitor.Environment.Cpus > 0);

        // Longer than SQL_TEXT, which keeps 1000 bytes – with umlauts about 500 characters (second review of WP-75): the list
        // marks it as maybe cut, the details bring the whole text.
        var sql = $"SELECT /* fs_load_{Guid.NewGuid():N} {new string('ä', 700)} */ COUNT(*) FROM all_objects WHERE ROWNUM <= 50";
        await using (var other = new OracleConnection(oracle.RequireConnectionString()))
        {
            await other.OpenAsync(Ct);
            for (var i = 0; i < 12; i++)
            {
                await using var command = other.CreateCommand();
                command.CommandText = sql;
                await command.ExecuteScalarAsync(Ct);
            }
        }

        await Task.Delay(TimeSpan.FromSeconds(1.2), Ct); // the next look must be at least a second later (LAST_ACTIVE_TIME)
        var sample = await monitor.LookAsync(null, Ct);

        Assert.NotNull(sample);
        Assert.True(sample.ExecutionsPerSecond > 0);
        var sqlId = Plans.SqlId(sql);

        // V$SQLSTATS updates its figures with a delay (CI on main after WP-75: 4 of 12 executions right after): the next looks
        // bring the rest. 10 s were not always enough (CI on main after 3.67.0, the third time); up to StatsLag, as long as a
        // look reads late figures again, the page counts them.
        StatementRow? row = null;
        var looks = new List<string> { await CursorStateAsync(sqlId, monitor.Tracker, "first look") };
        try
        {
            await Wait.UntilAsync(async () =>
            {
                row = monitor.Tracker.Top(null, TopStatementSort.Executions, StatementFilter.None, 10_000).SingleOrDefault(r => r.SqlId == sqlId);
                if (row?.Figures.Executions >= 12)
                {
                    return true;
                }

                await Task.Delay(TimeSpan.FromSeconds(1.1), Ct); // a second apart: LAST_ACTIVE_TIME has whole seconds
                var since = ServerLoadMonitor.ActiveSince(monitor.Tracker.StatementsSince!.Value);
                await monitor.LookAsync(null, Ct);
                looks.Add(await CursorStateAsync(sqlId, monitor.Tracker, $"look since {since:HH:mm:ss}"));
                return false;
            }, "all 12 executions in the window", ServerLoadMonitor.StatsLag);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // What the server held at each look, to tell late figures from figures the look missed (CI on main after 3.67.0).
            Assert.Fail($"{e.Message} SQL_ID {sqlId}:{Environment.NewLine}{string.Join(Environment.NewLine, looks)}");
        }

        Assert.Equal(12, row!.Figures.Executions);
        Assert.StartsWith(row.Info!.Text!, sql, StringComparison.Ordinal);
        Assert.True(row.Info.Text!.Length < 1000);
        Assert.True(row.Info.MayBeCut);
        Assert.Equal(Owner, row.Info?.Schema);

        Assert.Equal(new StatementText(sql, false), await connection.ServerLoad.ReadFullTextAsync(sqlId, Ct));
        Assert.Null(await connection.ServerLoad.ReadFullTextAsync("0000000000000", Ct));
        var cursor = await connection.Schema.GetCursorByIdAsync(sqlId, Ct);
        Assert.Equal(sql, cursor!.Plan.Sql);
        Assert.NotEmpty(cursor.Plan.Steps);
        Assert.Null(await connection.Schema.GetCursorByIdAsync("0000000000000", Ct));
    }

    [Theory]
    [InlineData(TopStatementSort.Cpu)]
    [InlineData(TopStatementSort.CpuPerExecution)]
    [InlineData(TopStatementSort.Elapsed)]
    [InlineData(TopStatementSort.ElapsedPerExecution)]
    [InlineData(TopStatementSort.Executions)]
    [InlineData(TopStatementSort.BufferGets)]
    [InlineData(TopStatementSort.BufferGetsPerExecution)]
    [InlineData(TopStatementSort.DiskReads)]
    [InlineData(TopStatementSort.Rows)]
    [InlineData(TopStatementSort.UserIoWait)]
    [InlineData(TopStatementSort.ConcurrencyWait)]
    [InlineData(TopStatementSort.ApplicationWait)]
    public async Task The_list_since_loading_is_sorted_filtered_and_cut_on_the_server(TopStatementSort sort)
    {
        await using var connection = await OpenAsync();

        var rows = await connection.ServerLoad.ReadTopSinceLoadAsync(sort, new StatementFilter(Owner.ToLowerInvariant()), 5, Ct);
        var all = await connection.ServerLoad.ReadTopSinceLoadAsync(sort, StatementFilter.None, 5, Ct); // review: the top first, then V$SQL

        Assert.InRange(rows.Count, 1, 5); // this session's own statements at least
        Assert.All(rows, r => Assert.Contains(Owner, r.Info!.Schema, StringComparison.Ordinal));
        Assert.Equal(5, all.Count);
        foreach (var list in new[] { rows, all })
        {
            Assert.Equal(list.Select(r => r.Figures.ValueOf(sort)).OrderDescending(), list.Select(r => r.Figures.ValueOf(sort)));
            Assert.All(list, r => Assert.True(r.Plans >= 1));
        }
    }

    [Fact]
    public async Task A_waiting_session_is_listed_with_its_wait_and_the_reading_session_is_not()
    {
        await using var connection = await OpenAsync();
        await using var other = new OracleConnection(oracle.RequireConnectionString());
        await other.OpenAsync(Ct);
        await using var command = other.CreateCommand();
        command.CommandText = "BEGIN DBMS_SESSION.SLEEP(4); END;";
        var sleeping = command.ExecuteNonQueryAsync(Ct);

        IReadOnlyList<ActiveSession> sessions = [];
        await Wait.UntilAsync(async () =>
        {
            sessions = await connection.ServerLoad.ReadActiveSessionsAsync(Ct);
            return sessions.Any(s => s.Event is not null && s.Event.Contains("timer", StringComparison.OrdinalIgnoreCase));
        }, "the sleeping session");
        await sleeping;

        var waiting = sessions.First(s => s.Event?.Contains("timer", StringComparison.OrdinalIgnoreCase) == true);
        Assert.Equal(Owner, waiting.User);
        Assert.False(waiting.OnCpu);
        Assert.DoesNotContain(sessions, s => s.ClientInfo == connection.SessionTag);
    }

    [Fact]
    public async Task Without_rights_on_the_v_views_the_reader_says_what_is_missing()
    {
        await using var connection = await OpenAsync(NoRightsUser, NoRightsPassword);

        var refused = await Assert.ThrowsAsync<ServerLoadUnavailableException>(() => connection.ServerLoad.ReadEnvironmentAsync(Ct));

        Assert.Contains("SELECT_CATALOG_ROLE", refused.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<PlanUnavailableException>(() => connection.Schema.GetCursorByIdAsync("0000000000000", Ct));
    }
}
