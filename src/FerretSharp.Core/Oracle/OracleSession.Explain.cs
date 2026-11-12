using System.Data.Common;
using FerretSharp.Core.Connections;
using FerretSharp.Core.Query;
using FerretSharp.Core.Resources;

namespace FerretSharp.Core.Oracle;

/// <summary>The estimated plan of a query (ADR 0012): the one statement besides queries that leaves the read path.</summary>
public sealed partial class OracleSession
{
    /// <summary>
    /// The global temporary table behind the public synonym <c>PLAN_TABLE</c>, named directly (A37): a schema can have
    /// a <c>PLAN_TABLE</c> of its own (an old <c>utlxplan.sql</c>), a heap table that hides the synonym – without a
    /// transaction, ODP.NET would commit the plan rows into it, also on Prod. Oracle grants it to PUBLIC.
    /// </summary>
    private const string PlanTable = "SYS.PLAN_TABLE$";

    /// <summary>The rows <c>EXPLAIN PLAN</c> wrote for one statement id, in the columns <see cref="OraclePlans.ReadAsync"/> reads.</summary>
    private const string PlanTableSql = $"""
        SELECT {OraclePlans.StepColumns}
          FROM {PlanTable}
         WHERE STATEMENT_ID = :id
         ORDER BY ID
        """;

    /// <summary>Whether this session may use <see cref="PlanTable"/> – asked only after <c>EXPLAIN PLAN</c> failed for want of rights.</summary>
    private const string PlanTableProbeSql = $"SELECT COUNT(*) FROM {PlanTable} WHERE 1 = 0";

    /// <summary>
    /// The optimizer's plan of a plain query without running it: <c>EXPLAIN PLAN … INTO SYS.PLAN_TABLE$</c> writes the plan
    /// into the global temporary table, private to this session, under an id made here; <paramref name="read"/> gets those
    /// rows (<c>ID</c>, <c>PARENT_ID</c>, <c>DEPTH</c>, <c>OPERATION</c> …, ordered by id), then they are deleted. Only
    /// these statements, built here – never a <c>PLAN_TABLE</c> of the schema (A37). The bind placeholders stay unbound:
    /// Oracle plans them as text. Not in a read-only transaction: Oracle refuses there.
    /// </summary>
    /// <exception cref="RefusedException">
    /// Not a plain query, the session is locked (read-only transaction), or the session may not use <c>SYS.PLAN_TABLE$</c>.
    /// </exception>
    internal async Task<T> ExplainPlanAsync<T>(string sql, Func<DbDataReader, CancellationToken, Task<T>> read, CancellationToken cancellationToken)
    {
        if (!IsReadOnlyStatement(sql))
        {
            throw new RefusedException(OracleText.ExplainOnlyQueries);
        }

        return await ExclusiveAsync(async () =>
        {
            if (_readOnlySnapshots || Transaction.Mode == TransactionMode.ReadOnly)
            {
                throw new RefusedException(OracleText.ExplainInReadOnlyTransaction);
            }

            var id = "FS" + Guid.NewGuid().ToString("N")[..24]; // STATEMENT_ID is a literal, at most 30 characters
            QueryParameter[] byId = [new("id", id)];
            try
            {
                try
                {
                    await RunAsync($"EXPLAIN PLAN SET STATEMENT_ID = '{id}' INTO {PlanTable} FOR {sql.TrimEnd().TrimEnd(';')}", [],
                        (command, ct) => command.ExecuteNonQueryAsync(ct), cancellationToken);
                }
                catch (OracleStatementException ex) when (ex.Oracle is { } oracle && OracleErrorCodes.MissingRights.Contains(oracle.Number))
                {
                    // ORA-00942 may as well name a table of the query; only a failing probe blames the plan table.
                    if (!await PlanTableUsableAsync(cancellationToken))
                    {
                        throw new RefusedException(OracleText.ExplainNoPlanTable, ex);
                    }

                    throw;
                }

                return await ReadCoreAsync(PlanTableSql, byId, read, cancellationToken);
            }
            finally
            {
                try
                {
                    await RunAsync($"DELETE FROM {PlanTable} WHERE STATEMENT_ID = :id", byId, (command, ct) => command.ExecuteNonQueryAsync(ct), CancellationToken.None);
                }
                catch (OracleStatementException)
                {
                    // the rows are private to the session and vanish with it
                }
            }
        }, cancellationToken);
    }

    /// <summary>
    /// False only if the probe fails for want of rights; any other failure (the connection lost meanwhile) goes to the caller as
    /// it is – it was taken for a missing grant, without the banner of a lost connection (code review).
    /// </summary>
    private async Task<bool> PlanTableUsableAsync(CancellationToken cancellationToken)
    {
        try
        {
            await RunAsync(PlanTableProbeSql, [], (command, ct) => command.ExecuteScalarAsync(ct), cancellationToken);
            return true;
        }
        catch (OracleStatementException ex) when (ex.Oracle is { } oracle && OracleErrorCodes.MissingRights.Contains(oracle.Number))
        {
            return false;
        }
    }
}
