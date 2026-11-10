using FerretSharp.Core.Query;
using FerretSharp.Testing;
using Oracle.ManagedDataAccess.Client;

namespace FerretSharp.Integration.Tests;

/// <summary>
/// Oracle frees the plan of a cursor (heap 6) before the cursor itself: V$SQL keeps the children with their figures,
/// V$SQL_PLAN_STATISTICS_ALL has no rows for them until the next execution loads the plan again. Under load a cursor can
/// also leave V$SQL altogether within seconds (test container, 24 sessions of distinct statements: gone after 4 s) – also
/// between the lookup's reads. CI saw text and figures without plan within three seconds of the run (2026-11-10,
/// <c>fix-cursor-plan-child</c>), so a test that looks up another session's cursor runs the statement again meanwhile.
/// </summary>
internal static class CursorPlans
{
    /// <summary>
    /// The plan of the cursor <paramref name="read"/> finds; while there is none (no steps, no cursor any more, or the cursor
    /// gone between the lookup's reads – <see cref="PlanUnavailableException"/>), <paramref name="runAgain"/> runs the
    /// statement once more and the cursor is read again. A timeout names the last refusal (missing rights stay visible).
    /// </summary>
    public static async Task<ExecutionPlan> ReadAsync(Func<Task<CursorInfo?>> read, Func<Task> runAgain)
    {
        ExecutionPlan? plan = null;
        PlanUnavailableException? refused = null;
        try
        {
            await Wait.UntilAsync(async () =>
            {
                try
                {
                    if (await read() is { HasPlan: true } cursor)
                    {
                        plan = cursor.Plan;
                        return true;
                    }
                }
                catch (PlanUnavailableException ex)
                {
                    refused = ex;
                }

                await runAgain();
                return false;
            }, "the plan of the cursor");
        }
        catch (Exception e) when (e is not OperationCanceledException && refused is not null)
        {
            Assert.Fail($"{e.Message} Last refusal: {refused.Message}");
        }

        return plan!;
    }

    /// <summary>
    /// Frees the plans of all children of <paramref name="sqlId"/> as Oracle does under memory pressure (heap 6 only, the
    /// children stay); <paramref name="system"/> connects as SYSTEM, which may run DBMS_SHARED_POOL. False when the cursor
    /// is not in the shared pool (any more) – under the suite's load it can be gone within seconds.
    /// </summary>
    public static async Task<bool> DropAsync(string system, string sqlId, CancellationToken cancellationToken)
    {
        await using var connection = new OracleConnection(system);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.BindByName = true;
        command.CommandText = """
            DECLARE
              v_name VARCHAR2(100);
            BEGIN
              SELECT RAWTOHEX(address) || ',' || hash_value INTO v_name FROM v$sqlarea WHERE sql_id = :sql_id;
              SYS.DBMS_SHARED_POOL.PURGE(v_name, 'C', 64);
              :dropped := 1;
            EXCEPTION
              WHEN NO_DATA_FOUND THEN :dropped := 0;
            END;
            """;
        command.Parameters.Add(new OracleParameter("sql_id", sqlId));
        var dropped = command.Parameters.Add(new OracleParameter("dropped", OracleDbType.Int32, System.Data.ParameterDirection.Output));
        await command.ExecuteNonQueryAsync(cancellationToken);
        return dropped.Value?.ToString() == "1";
    }
}
