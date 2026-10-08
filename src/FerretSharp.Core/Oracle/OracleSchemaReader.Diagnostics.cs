using FerretSharp.Core.Connections;
using FerretSharp.Core.Data;
using FerretSharp.Core.Query;
using FerretSharp.Core.Schema;
using static FerretSharp.Core.Oracle.OracleReading;

namespace FerretSharp.Core.Oracle;

/// <summary>What is not schema metadata but goes through the explorer session: estimated plans and lock holders.</summary>
public sealed partial class OracleSchemaReader
{
    // TM locks on the table by other sessions; row locks themselves are not listed by Oracle.
    private const string LockHoldersSql = """
        SELECT DISTINCT s.sid, s.username, s.osuser, s.machine, s.program, s.module, s.action, s.logon_time
          FROM v$locked_object lo
          JOIN v$session s ON s.sid = lo.session_id
          JOIN all_objects o ON o.object_id = lo.object_id
         WHERE o.owner = :owner AND o.object_name = :name AND o.object_type = 'TABLE'
           AND s.sid <> SYS_CONTEXT('USERENV', 'SID')
         ORDER BY s.sid
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

    public async Task<IReadOnlyList<LockHolder>?> GetLockHoldersAsync(TableRef table, CancellationToken cancellationToken)
    {
        try
        {
            return await session.ReadListAsync(LockHoldersSql, [new("owner", table.Owner), new("name", table.Name)], reader =>
                new LockHolder(Int(reader, 0) ?? 0, Text(reader, 1), Text(reader, 2), Text(reader, 3), Text(reader, 4),
                    Text(reader, 5), Text(reader, 6), Date(reader, 7)), cancellationToken);
        }
        catch (DatabaseException ex) when (ex.IsAny(OracleErrorCodes.MissingRights))
        {
            return null; // no access to the V$ views
        }
    }
}
