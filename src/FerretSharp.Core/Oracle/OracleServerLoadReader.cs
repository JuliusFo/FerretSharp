using System.Data.Common;
using FerretSharp.Core.Connections;
using FerretSharp.Core.Query;
using FerretSharp.Core.Resources;
using FerretSharp.Core.ServerLoad;
using static FerretSharp.Core.Oracle.OracleReading;

namespace FerretSharp.Core.Oracle;

/// <summary>
/// The server load from the V$ views outside the management packs (WP-75, ADR 0045). The counters come as one row of scalar
/// subqueries: one round trip per look, and <c>MAX</c> because a container database may show a row per container.
/// </summary>
internal sealed class OracleServerLoadReader(OracleSession session) : IServerLoadReader
{
    // Every view the page reads, so a missing right shows before the baseline is read (ORA-00942 fails the whole statement).
    public const string EnvironmentSql = """
        SELECT TO_NUMBER(SYS_CONTEXT('USERENV', 'CON_ID')),
               (SELECT COUNT(*) FROM gv$instance),
               (SELECT MAX(value) FROM v$osstat WHERE stat_name = 'NUM_CPUS'),
               (SELECT COUNT(*) FROM v$sqlstats WHERE ROWNUM = 1)
             + (SELECT COUNT(*) FROM v$sql WHERE ROWNUM = 1)
             + (SELECT COUNT(*) FROM v$sys_time_model WHERE ROWNUM = 1)
             + (SELECT COUNT(*) FROM v$sysstat WHERE ROWNUM = 1)
             + (SELECT COUNT(*) FROM v$system_wait_class WHERE ROWNUM = 1)
             + (SELECT COUNT(*) FROM v$instance WHERE ROWNUM = 1)
             + (SELECT COUNT(*) FROM v$session WHERE ROWNUM = 1)
          FROM dual
        """;

    // SYSTIMESTAMP in the server's time zone, like LAST_ACTIVE_TIME; as TIMESTAMP, which ODP.NET reads as DateTime.
    public const string CountersSql = """
        SELECT CAST(SYSTIMESTAMP AS TIMESTAMP),
               (SELECT MAX(startup_time) FROM v$instance),
               (SELECT MAX(value) FROM v$osstat WHERE stat_name = 'NUM_CPUS'),
               (SELECT MAX(value) FROM v$osstat WHERE stat_name = 'BUSY_TIME'),
               (SELECT MAX(value) FROM v$osstat WHERE stat_name = 'IDLE_TIME'),
               (SELECT MAX(value) FROM v$sys_time_model WHERE stat_name = 'DB time'),
               (SELECT MAX(value) FROM v$sys_time_model WHERE stat_name = 'DB CPU'),
               (SELECT MAX(value) FROM v$sysstat WHERE name = 'execute count'),
               (SELECT MAX(value) FROM v$sysstat WHERE name = 'session logical reads'),
               (SELECT MAX(value) FROM v$sysstat WHERE name = 'user commits')
          FROM dual
        """;

    // TIME_WAITED_FG: foreground sessions only (what users wait for), in centiseconds.
    public const string WaitClassesSql = """
        SELECT wait_class, MAX(time_waited_fg)
          FROM v$system_wait_class
         WHERE wait_class <> 'Idle'
         GROUP BY wait_class
        """;

    private const string StatementColumns = """
        sql_id, plan_hash_value, con_id, executions, cpu_time, elapsed_time, buffer_gets, disk_reads, rows_processed, parse_calls,
               user_io_wait_time, concurrency_wait_time, application_wait_time
        """;

    public const string AllStatementsSql = $"""
        SELECT {StatementColumns}
          FROM v$sqlstats
        """;

    public const string StatementsSinceSql = $"""
        SELECT {StatementColumns}
          FROM v$sqlstats
         WHERE last_active_time >= :since
        """;

    /// <summary>The columns of <see cref="StatementFigures"/> in their order.</summary>
    private static readonly string[] FigureColumns =
    [
        "executions", "cpu_time", "elapsed_time", "buffer_gets", "disk_reads", "rows_processed", "parse_calls",
        "user_io_wait_time", "concurrency_wait_time", "application_wait_time",
    ];

    public const string ActiveSessionsSql = """
        SELECT sid, serial#, username, osuser, machine, program, module, client_info, sql_id,
               CASE WHEN state = 'WAITING' THEN event END, CASE WHEN state = 'WAITING' THEN wait_class END,
               last_call_et, CASE WHEN blocking_session_status = 'VALID' THEN blocking_session END
          FROM v$session
         WHERE status = 'ACTIVE' AND type = 'USER' AND sid <> SYS_CONTEXT('USERENV', 'SID')
         ORDER BY last_call_et DESC, sid
         FETCH FIRST 200 ROWS ONLY
        """;

    /// <summary>
    /// The top list since loading, with the sort column filled in (<see cref="SortExpression"/>, never text from outside).
    /// The figures from <c>V$SQLSTATS</c>, summed over the plans: it keeps them after a cursor left the library cache, where
    /// <c>V$SQLAREA</c> already lost it (E2E of WP-75: the most expensive statement of a finished batch was missing).
    /// Module and schema come from <c>V$SQL</c>; a statement no longer there has none and drops out of a filter.
    /// </summary>
    internal static string TopSinceLoadSql(TopStatementSort sort) => $"""
        SELECT s.sql_id, {string.Join(", ", FigureColumns.Select(c => "s." + c))}, s.plans, s.sql_text, i.module, i.parsing_schema_name, s.last_active_time
          FROM (SELECT sql_id, {string.Join(", ", FigureColumns.Select(c => $"SUM({c}) {c}"))},
                       COUNT(DISTINCT plan_hash_value) plans, MAX(sql_text) sql_text, MAX(last_active_time) last_active_time
                  FROM v$sqlstats
                 GROUP BY sql_id) s
          LEFT JOIN (SELECT sql_id, MAX(module) module, MAX(parsing_schema_name) parsing_schema_name
                       FROM v$sql
                      GROUP BY sql_id) i ON i.sql_id = s.sql_id
         WHERE (:schema_part IS NULL OR INSTR(UPPER(i.parsing_schema_name), UPPER(:schema_part)) > 0)
           AND (:module_part IS NULL OR INSTR(UPPER(i.module), UPPER(:module_part)) > 0)
         ORDER BY {SortExpression(sort)} DESC NULLS LAST, s.sql_id
         FETCH FIRST :top ROWS ONLY
        """;

    /// <summary>
    /// Text (from <c>V$SQLSTATS</c>, which keeps it longest), module and schema (from <c>V$SQL</c>) of up to
    /// <see cref="ServerLoadMonitor.InfoBatch"/> statements, one bind each.
    /// </summary>
    internal static string InfoSql(int count) => $"""
        SELECT s.sql_id, MAX(s.sql_text), MAX(i.module), MAX(i.parsing_schema_name)
          FROM v$sqlstats s
          LEFT JOIN v$sql i ON i.sql_id = s.sql_id
         WHERE s.sql_id IN ({string.Join(", ", Enumerable.Range(0, count).Select(i => $":id{i}"))})
         GROUP BY s.sql_id
        """;

    internal static string SortExpression(TopStatementSort sort) => sort switch
    {
        TopStatementSort.Cpu => "s.cpu_time",
        TopStatementSort.CpuPerExecution => "s.cpu_time / NULLIF(s.executions, 0)",
        TopStatementSort.Elapsed => "s.elapsed_time",
        TopStatementSort.ElapsedPerExecution => "s.elapsed_time / NULLIF(s.executions, 0)",
        TopStatementSort.Executions => "s.executions",
        TopStatementSort.BufferGets => "s.buffer_gets",
        TopStatementSort.BufferGetsPerExecution => "s.buffer_gets / NULLIF(s.executions, 0)",
        TopStatementSort.DiskReads => "s.disk_reads",
        TopStatementSort.Rows => "s.rows_processed",
        TopStatementSort.UserIoWait => "s.user_io_wait_time",
        TopStatementSort.ConcurrencyWait => "s.concurrency_wait_time",
        TopStatementSort.ApplicationWait => "s.application_wait_time",
        _ => throw new ArgumentOutOfRangeException(nameof(sort), sort, null),
    };

    public Task<ServerEnvironment> ReadEnvironmentAsync(CancellationToken cancellationToken) =>
        GuardAsync(async () =>
        {
            var rows = await session.ReadListAsync(EnvironmentSql, [],
                reader => new ServerEnvironment(Int(reader, 0) ?? 0, Int(reader, 1) ?? 1, Int(reader, 2) ?? 0), cancellationToken);
            return rows[0];
        });

    public Task<LoadCounters> ReadCountersAsync(CancellationToken cancellationToken) =>
        GuardAsync(async () =>
        {
            var waits = await session.ReadListAsync(WaitClassesSql, [],
                reader => (Class: Text(reader, 0) ?? "", Micros: (Long(reader, 1) ?? 0) * 10_000), cancellationToken);
            var rows = await session.ReadListAsync(CountersSql, [], reader => new LoadCounters(
                reader.GetDateTime(0),
                Date(reader, 1) ?? DateTime.MinValue,
                Int(reader, 2) ?? 0,
                Long(reader, 3),
                Long(reader, 4),
                Long(reader, 5) ?? 0,
                Long(reader, 6) ?? 0,
                Long(reader, 7) ?? 0,
                Long(reader, 8) ?? 0,
                Long(reader, 9) ?? 0,
                waits.ToDictionary(w => w.Class, w => w.Micros, StringComparer.Ordinal)), cancellationToken);
            return rows[0];
        });

    public Task<IReadOnlyList<StatementCounters>> ReadStatementsAsync(DateTime? activeSince, CancellationToken cancellationToken) =>
        GuardAsync<IReadOnlyList<StatementCounters>>(async () => activeSince is { } since
            ? await session.ReadListAsync(StatementsSinceSql, [new QueryParameter("since", since, OracleTypeHint.Date)], ReadCounters, cancellationToken, many: true)
            : await session.ReadListAsync(AllStatementsSql, [], ReadCounters, cancellationToken, many: true));

    public Task<IReadOnlyList<StatementInfo>> ReadStatementInfoAsync(IReadOnlyList<string> sqlIds, CancellationToken cancellationToken)
    {
        if (sqlIds.Count == 0)
        {
            return Task.FromResult<IReadOnlyList<StatementInfo>>([]);
        }

        return GuardAsync<IReadOnlyList<StatementInfo>>(async () => await session.ReadListAsync(
            InfoSql(sqlIds.Count), [.. sqlIds.Select((id, i) => new QueryParameter($"id{i}", id))],
            reader => new StatementInfo(Text(reader, 0) ?? "", Text(reader, 1), Text(reader, 2), Text(reader, 3)), cancellationToken));
    }

    public Task<IReadOnlyList<StatementRow>> ReadTopSinceLoadAsync(TopStatementSort sort, StatementFilter filter, int count, CancellationToken cancellationToken) =>
        GuardAsync<IReadOnlyList<StatementRow>>(async () => await session.ReadListAsync(TopSinceLoadSql(sort),
            [
                new QueryParameter("schema_part", Part(filter.Schema)),
                new QueryParameter("module_part", Part(filter.Module)),
                new QueryParameter("top", count),
            ],
            reader =>
            {
                var sqlId = Text(reader, 0) ?? "";
                return new StatementRow(sqlId, Int(reader, 11), ReadFigures(reader, 1),
                    new StatementInfo(sqlId, Text(reader, 12), Text(reader, 13), Text(reader, 14)), Date(reader, 15));
            }, cancellationToken));

    public Task<IReadOnlyList<ActiveSession>> ReadActiveSessionsAsync(CancellationToken cancellationToken) =>
        GuardAsync<IReadOnlyList<ActiveSession>>(async () => await session.ReadListAsync(ActiveSessionsSql, [], reader => new ActiveSession(
            Int(reader, 0) ?? 0, Int(reader, 1) ?? 0, Text(reader, 2), Text(reader, 3), Text(reader, 4), Text(reader, 5), Text(reader, 6),
            Text(reader, 7), Text(reader, 8), Text(reader, 9), Text(reader, 10), TimeSpan.FromSeconds(Long(reader, 11) ?? 0), Int(reader, 12)),
            cancellationToken));

    private static string? Part(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static StatementCounters ReadCounters(DbDataReader reader) =>
        new(Text(reader, 0) ?? "", Long(reader, 1) ?? 0, Int(reader, 2) ?? 0, ReadFigures(reader, 3));

    /// <summary>The figures in the order of <see cref="FigureColumns"/> from <paramref name="first"/> on.</summary>
    private static StatementFigures ReadFigures(DbDataReader reader, int first) =>
        new(Long(reader, first) ?? 0, Long(reader, first + 1) ?? 0, Long(reader, first + 2) ?? 0, Long(reader, first + 3) ?? 0,
            Long(reader, first + 4) ?? 0, Long(reader, first + 5) ?? 0, Long(reader, first + 6) ?? 0, Long(reader, first + 7) ?? 0,
            Long(reader, first + 8) ?? 0, Long(reader, first + 9) ?? 0);

    private static async Task<T> GuardAsync<T>(Func<Task<T>> read)
    {
        try
        {
            return await read();
        }
        catch (DatabaseException ex) when (ex.IsAny(OracleErrorCodes.MissingRights))
        {
            throw new ServerLoadUnavailableException(OracleText.ServerLoadNoRights, ex);
        }
    }
}
