using System.Data.Common;
using FerretSharp.Core.Connections;
using FerretSharp.Core.Data;
using FerretSharp.Core.Query;
using FerretSharp.Core.Resources;
using FerretSharp.Core.Schema;
using static FerretSharp.Core.Oracle.OracleReading;

namespace FerretSharp.Core.Oracle;

/// <summary>
/// What is not schema metadata but goes through the explorer session: estimated plans, cursors of other programs, lock holders
/// and what a workspace's session is doing (WP-52).
/// </summary>
public sealed partial class OracleSchemaReader
{
    /// <summary>
    /// A session holding a lock, as <see cref="ReadLockHolder"/> reads it – one list for the lock holders and the blocker of a
    /// waiting session (R6b, review 3.40.0, B11: was written twice, against <c>.claude/rules/oracle.md</c>).
    /// </summary>
    private static string LockHolderColumns(string alias) =>
        string.Join(", ", new[] { "sid", "username", "osuser", "machine", "program", "module", "action", "logon_time", "client_info" }.Select(c => $"{alias}.{c}"));

    // TM locks on the table by other sessions; row locks themselves are not listed by Oracle.
    private static readonly string LockHoldersSql = $"""
        SELECT DISTINCT {LockHolderColumns("s")}
          FROM v$locked_object lo
          JOIN v$session s ON s.sid = lo.session_id
          JOIN all_objects o ON o.object_id = lo.object_id
         WHERE o.owner = :owner AND o.object_name = :name AND o.object_type = 'TABLE'
           AND s.sid <> SYS_CONTEXT('USERENV', 'SID')
         ORDER BY s.sid
        """;

    // One round trip per look (WP-52, ADR 0027): the session by its tag, the session blocking it with the table of the row it
    // waits for, and its latest unfinished long operation. V$SESSION shows the own RAC instance only, so the blocker is joined
    // only if it is there too (a SID alone is not unique across instances). ROW_WAIT_OBJ# is an object id (-1 when not waiting).
    // Review 3.40.0: BLOCKING_SESSION is also set for waits that are no lock – log file sync (LGWR), read by other session,
    // buffer busy waits, library cache (A10) – so only an enqueue of the Application class (enq: TX, enq: TM …) with a valid
    // blocker counts. A long operation counts only for the statement running now (A11): one of a cancelled earlier statement
    // stays unfinished in V$SESSION_LONGOPS of the same long-lived session.
    private static readonly string SessionActivitySql = $"""
        SELECT s.lock_holder, s.event, CASE WHEN s.state = 'WAITING' THEN s.wait_time_micro END,
               {LockHolderColumns("b")},
               o.owner, o.object_name,
               l.opname, l.target, l.sofar, l.totalwork, l.units, l.time_remaining
          FROM (SELECT x.sid, x.serial#, x.event, x.state, x.wait_time_micro, x.blocking_instance, x.row_wait_obj#, x.sql_id, x.sql_exec_id,
                       CASE WHEN x.blocking_session_status = 'VALID' AND x.wait_class = 'Application' THEN x.blocking_session END lock_holder
                  FROM v$session x
                 WHERE x.client_info = :tag) s
          LEFT JOIN v$session b
            ON b.sid = s.lock_holder AND s.blocking_instance = SYS_CONTEXT('USERENV', 'INSTANCE')
          LEFT JOIN all_objects o
            ON s.lock_holder IS NOT NULL AND o.object_id = s.row_wait_obj#
          OUTER APPLY (SELECT lo.opname, lo.target, lo.sofar, lo.totalwork, lo.units, lo.time_remaining
                         FROM v$session_longops lo
                        WHERE lo.sid = s.sid AND lo.serial# = s.serial# AND lo.sql_id = s.sql_id AND lo.sql_exec_id = s.sql_exec_id
                          AND lo.totalwork > 0 AND lo.sofar < lo.totalwork
                        ORDER BY lo.start_time DESC
                        FETCH FIRST 1 ROW ONLY) l
        """;

    /// <exception cref="PlanUnavailableException">Not a plain query, or the session is locked (read-only transaction).</exception>
    public async Task<ExecutionPlan> ExplainAsync(QuerySpec query, CancellationToken cancellationToken)
    {
        try
        {
            var steps = await session.ExplainPlanAsync(query.Sql, (reader, ct) => OraclePlans.ReadAsync(reader, actual: false, ct), cancellationToken);
            return new ExecutionPlan(PlanSource.Estimated, query.Sql, steps);
        }
        catch (RefusedException ex) when (ex is not PlanUnavailableException)
        {
            throw new PlanUnavailableException(ex.Message, ex);
        }
    }

    public async Task<CursorInfo?> GetCursorAsync(string sql, CancellationToken cancellationToken)
    {
        var sqlId = Plans.SqlId(sql);
        IReadOnlyList<QueryParameter> byId = [new("sql_id", sqlId)];
        try
        {
            var figures = await session.ReadListAsync(OraclePlans.CursorFigures, byId, reader => new
            {
                Children = Int(reader, 0) ?? 0,
                Executions = Long(reader, 1) ?? 0,
                Rows = Long(reader, 2) ?? 0,
                Gets = Long(reader, 3) ?? 0,
                Reads = Long(reader, 4) ?? 0,
                Elapsed = TimeSpan.FromMicroseconds(Long(reader, 5) ?? 0),
                Cpu = TimeSpan.FromMicroseconds(Long(reader, 6) ?? 0),
                LastActive = Date(reader, 7),
            }, cancellationToken);
            if (figures is not [{ Children: > 0 } cursor])
            {
                return null;
            }

            var (child, steps) = await OraclePlans.ReadActualAsync(session, sqlId, cancellationToken);
            return new CursorInfo(sqlId, cursor.Children, cursor.Executions, cursor.Rows, cursor.Gets, cursor.Reads, cursor.Elapsed, cursor.Cpu,
                cursor.LastActive, new ExecutionPlan(PlanSource.Actual, sql, steps, sqlId, child));
        }
        catch (DatabaseException ex) when (ex.IsAny(OracleErrorCodes.MissingRights))
        {
            throw new PlanUnavailableException(OracleText.CursorNoRights, ex);
        }
    }

    public async Task<IReadOnlyList<LockHolder>?> GetLockHoldersAsync(TableRef table, CancellationToken cancellationToken)
    {
        try
        {
            return await session.ReadListAsync(LockHoldersSql, [new("owner", table.Owner), new("name", table.Name)], reader => ReadLockHolder(reader, 0), cancellationToken);
        }
        catch (DatabaseException ex) when (ex.IsAny(OracleErrorCodes.MissingRights))
        {
            return null; // no access to the V$ views
        }
    }

    public async Task<SessionActivity?> GetSessionActivityAsync(string sessionTag, CancellationToken cancellationToken)
    {
        try
        {
            var rows = await session.ReadListAsync(SessionActivitySql, [new("tag", sessionTag)], ReadActivity, cancellationToken);
            return rows is [var activity, ..] ? activity : SessionActivity.None;
        }
        catch (DatabaseException ex) when (ex.IsAny(OracleErrorCodes.MissingRights))
        {
            return null; // no access to the V$ views
        }
    }

    /// <summary>The columns of <see cref="LockHolderColumns"/> from <paramref name="first"/> on.</summary>
    private static LockHolder ReadLockHolder(DbDataReader reader, int first) =>
        new(Int(reader, first) ?? 0, Text(reader, first + 1), Text(reader, first + 2), Text(reader, first + 3), Text(reader, first + 4),
            Text(reader, first + 5), Text(reader, first + 6), Date(reader, first + 7), Text(reader, first + 8));

    private static SessionActivity ReadActivity(DbDataReader reader)
    {
        SessionWait? wait = null;
        if (Int(reader, 0) is { } blockerSid)
        {
            var blocker = Int(reader, 3) is not null ? ReadLockHolder(reader, 3) : null;
            var table = Text(reader, 12) is { } owner && Text(reader, 13) is { } name ? new TableRef(owner, name) : null;
            var waited = Long(reader, 2) is { } micro ? TimeSpan.FromMicroseconds(micro) : (TimeSpan?)null;
            wait = new SessionWait(Text(reader, 1) ?? "", waited, blockerSid, blocker, table);
        }

        LongOperation? progress = null;
        if (Text(reader, 14) is { } operation && Long(reader, 17) is { } total && total > 0)
        {
            var remaining = Long(reader, 19) is { } seconds ? TimeSpan.FromSeconds(seconds) : (TimeSpan?)null;
            progress = new LongOperation(operation, Text(reader, 15), Long(reader, 16) ?? 0, total, Text(reader, 18), remaining);
        }

        return new SessionActivity(wait, progress);
    }
}
