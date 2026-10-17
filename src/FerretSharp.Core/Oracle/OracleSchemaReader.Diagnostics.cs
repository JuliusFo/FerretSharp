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
    // TM locks on the table by other sessions; row locks themselves are not listed by Oracle.
    private const string LockHoldersSql = """
        SELECT DISTINCT s.sid, s.username, s.osuser, s.machine, s.program, s.module, s.action, s.logon_time, s.client_info
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
    private const string SessionActivitySql = """
        SELECT s.blocking_session, s.event, CASE WHEN s.state = 'WAITING' THEN s.wait_time_micro END,
               b.sid, b.username, b.osuser, b.machine, b.program, b.module, b.action, b.logon_time, b.client_info,
               o.owner, o.object_name,
               l.opname, l.target, l.sofar, l.totalwork, l.units, l.time_remaining
          FROM v$session s
          LEFT JOIN v$session b
            ON b.sid = s.blocking_session AND s.blocking_instance = SYS_CONTEXT('USERENV', 'INSTANCE')
          LEFT JOIN all_objects o
            ON s.blocking_session IS NOT NULL AND o.object_id = s.row_wait_obj#
          OUTER APPLY (SELECT lo.opname, lo.target, lo.sofar, lo.totalwork, lo.units, lo.time_remaining
                         FROM v$session_longops lo
                        WHERE lo.sid = s.sid AND lo.serial# = s.serial# AND lo.totalwork > 0 AND lo.sofar < lo.totalwork
                        ORDER BY lo.start_time DESC
                        FETCH FIRST 1 ROW ONLY) l
         WHERE s.client_info = :tag
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
            return await session.ReadListAsync(LockHoldersSql, [new("owner", table.Owner), new("name", table.Name)], reader =>
                new LockHolder(Int(reader, 0) ?? 0, Text(reader, 1), Text(reader, 2), Text(reader, 3), Text(reader, 4),
                    Text(reader, 5), Text(reader, 6), Date(reader, 7), Text(reader, 8)), cancellationToken);
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

    private static SessionActivity ReadActivity(DbDataReader reader)
    {
        SessionWait? wait = null;
        if (Int(reader, 0) is { } blockerSid)
        {
            var blocker = Int(reader, 3) is { } sid
                ? new LockHolder(sid, Text(reader, 4), Text(reader, 5), Text(reader, 6), Text(reader, 7), Text(reader, 8),
                    Text(reader, 9), Date(reader, 10), Text(reader, 11))
                : null;
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
