using System.Data.Common;
using FerretSharp.Core.Connections;
using FerretSharp.Core.Query;

namespace FerretSharp.Core.Oracle;

/// <summary>The estimated plan of a query (ADR 0012): the one statement besides queries that leaves the read path.</summary>
public sealed partial class OracleSession
{
    /// <summary>
    /// The optimizer's plan of a plain query without running it: <c>EXPLAIN PLAN</c> writes the plan into <c>PLAN_TABLE</c>
    /// – a global temporary table, private to this session – under an id made here; <paramref name="read"/> gets those rows
    /// (<c>ID</c>, <c>PARENT_ID</c>, <c>DEPTH</c>, <c>OPERATION</c> …, ordered by id), then they are deleted. Only these
    /// statements, built here. The bind placeholders stay unbound: Oracle plans them as text. Not in a read-only
    /// transaction: Oracle refuses there.
    /// </summary>
    /// <exception cref="InvalidOperationException">Not a plain query.</exception>
    /// <exception cref="RefusedException">The session is locked (read-only transaction).</exception>
    internal async Task<T> ExplainPlanAsync<T>(string sql, Func<DbDataReader, CancellationToken, Task<T>> read, CancellationToken cancellationToken)
    {
        if (!IsReadOnlyStatement(sql))
        {
            throw new InvalidOperationException("Nur Abfragen (SELECT/WITH) lassen sich erklären.");
        }

        return await ExclusiveAsync(async () =>
        {
            if (_readOnlySnapshots || Transaction.Mode == TransactionMode.ReadOnly)
            {
                throw new RefusedException("In einer READ ONLY-Transaktion lehnt Oracle EXPLAIN PLAN ab.");
            }

            var id = "FS" + Guid.NewGuid().ToString("N")[..24]; // STATEMENT_ID is a literal, at most 30 characters
            QueryParameter[] byId = [new("id", id)];
            try
            {
                await RunAsync($"EXPLAIN PLAN SET STATEMENT_ID = '{id}' FOR {sql.TrimEnd().TrimEnd(';')}", [],
                    (command, ct) => command.ExecuteNonQueryAsync(ct), cancellationToken);
                return await ReadCoreAsync(
                    """
                    SELECT ID, PARENT_ID, DEPTH, OPERATION, OPTIONS, OBJECT_OWNER, OBJECT_NAME, COST, CARDINALITY, BYTES,
                           ACCESS_PREDICATES, FILTER_PREDICATES
                      FROM PLAN_TABLE
                     WHERE STATEMENT_ID = :id
                     ORDER BY ID
                    """,
                    byId, read, cancellationToken);
            }
            finally
            {
                try
                {
                    await RunAsync("DELETE FROM PLAN_TABLE WHERE STATEMENT_ID = :id", byId, (command, ct) => command.ExecuteNonQueryAsync(ct), CancellationToken.None);
                }
                catch (OracleStatementException)
                {
                    // the rows are private to the session and vanish with it
                }
            }
        }, cancellationToken);
    }
}
