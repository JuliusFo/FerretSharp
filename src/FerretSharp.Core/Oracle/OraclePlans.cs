using System.Data.Common;
using FerretSharp.Core.Query;
using FerretSharp.Core.Resources;
using FerretSharp.Core.Schema;
using static FerretSharp.Core.Oracle.OracleReading;

namespace FerretSharp.Core.Oracle;

/// <summary>Reading plans (ADR 0012): rows of <c>PLAN_TABLE</c> or <c>V$SQL_PLAN_STATISTICS_ALL</c> as <see cref="PlanStep"/>s.</summary>
internal static class OraclePlans
{
    /// <summary>The columns both sources share, in this order; actual plans add the four LAST_* columns.</summary>
    public const string StepColumns =
        "ID, DEPTH, OPERATION, OPTIONS, OBJECT_OWNER, OBJECT_NAME, COST, CARDINALITY, BYTES, ACCESS_PREDICATES, FILTER_PREDICATES";

    public const string ActualSteps = $"""
        SELECT {StepColumns}, LAST_OUTPUT_ROWS, LAST_STARTS, LAST_ELAPSED_TIME, LAST_CR_BUFFER_GETS
          FROM V$SQL_PLAN_STATISTICS_ALL
         WHERE SQL_ID = :sql_id AND CHILD_NUMBER = :child
         ORDER BY ID
        """;

    /// <summary>The cursor just run: the most recently active child of the SQL_ID.</summary>
    public const string LatestChild = """
        SELECT CHILD_NUMBER
          FROM V$SQL
         WHERE SQL_ID = :sql_id
         ORDER BY LAST_ACTIVE_TIME DESC, CHILD_NUMBER DESC
         FETCH FIRST 1 ROWS ONLY
        """;

    /// <summary>The figures of a statement over all its children (WP-33); times in microseconds.</summary>
    public const string CursorFigures = """
        SELECT COUNT(*), SUM(EXECUTIONS), SUM(ROWS_PROCESSED), SUM(BUFFER_GETS), SUM(DISK_READS),
               SUM(ELAPSED_TIME), SUM(CPU_TIME), MAX(LAST_ACTIVE_TIME)
          FROM V$SQL
         WHERE SQL_ID = :sql_id
        """;

    /// <summary>
    /// The actual plan of the most recently active child cursor of <paramref name="sqlId"/> (R5b: read the same way for the
    /// explain plan and the app statements, which took child 0 if none was found).
    /// </summary>
    /// <exception cref="PlanUnavailableException">No child cursor: aged out of the shared pool meanwhile.</exception>
    public static async Task<(int Child, IReadOnlyList<PlanStep> Steps)> ReadActualAsync(OracleSession session, string sqlId, CancellationToken cancellationToken)
    {
        var child = await session.ExecuteReaderAsync(LatestChild, [new QueryParameter("sql_id", sqlId)], async (reader, ct) =>
                        await reader.ReadAsync(ct) ? Int(reader, 0) : null, cancellationToken)
                    ?? throw new PlanUnavailableException(TextFormat.Format(OracleText.CursorGone, sqlId));
        var steps = await session.ExecuteReaderAsync(ActualSteps, [new QueryParameter("sql_id", sqlId), new QueryParameter("child", child)],
            (reader, ct) => ReadAsync(reader, actual: true, ct), cancellationToken);
        return (child, steps);
    }

    /// <summary>Probes the rights the actual plan needs before running anything.</summary>
    public const string RightsProbe = "SELECT (SELECT COUNT(*) FROM V$SQL WHERE ROWNUM = 1) + (SELECT COUNT(*) FROM V$SQL_PLAN_STATISTICS_ALL WHERE ROWNUM = 1) FROM DUAL";

    public static async Task<IReadOnlyList<PlanStep>> ReadAsync(DbDataReader reader, bool actual, CancellationToken cancellationToken)
    {
        var steps = new List<PlanStep>();
        while (await reader.ReadAsync(cancellationToken))
        {
            steps.Add(new PlanStep(
                Int(reader, 0) ?? 0,
                Int(reader, 1) ?? 0,
                Text(reader, 2) ?? "",
                Text(reader, 3),
                Text(reader, 4),
                Text(reader, 5),
                Long(reader, 6),
                Long(reader, 7),
                Long(reader, 8),
                Text(reader, 9),
                Text(reader, 10),
                actual ? Long(reader, 11) : null,
                actual ? Long(reader, 12) : null,
                actual && Long(reader, 13) is { } microseconds ? TimeSpan.FromMicroseconds(microseconds) : null,
                actual ? Long(reader, 14) : null));
        }

        return steps;
    }
}
