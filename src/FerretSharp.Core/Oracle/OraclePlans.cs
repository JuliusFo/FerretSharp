using System.Data.Common;
using System.Globalization;
using FerretSharp.Core.Query;

namespace FerretSharp.Core.Oracle;

/// <summary>Reading plans (ADR 0012): rows of <c>PLAN_TABLE</c> or <c>V$SQL_PLAN_STATISTICS_ALL</c> as <see cref="PlanStep"/>s.</summary>
internal static class OraclePlans
{
    /// <summary>The columns both sources share, in this order; actual plans add the four LAST_* columns.</summary>
    public const string StepColumns =
        "ID, PARENT_ID, DEPTH, OPERATION, OPTIONS, OBJECT_OWNER, OBJECT_NAME, COST, CARDINALITY, BYTES, ACCESS_PREDICATES, FILTER_PREDICATES";

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

    /// <summary>Probes the rights the actual plan needs before running anything.</summary>
    public const string RightsProbe = "SELECT (SELECT COUNT(*) FROM V$SQL WHERE ROWNUM = 1) + (SELECT COUNT(*) FROM V$SQL_PLAN_STATISTICS_ALL WHERE ROWNUM = 1) FROM DUAL";

    public static async Task<IReadOnlyList<PlanStep>> ReadAsync(DbDataReader reader, bool actual, CancellationToken cancellationToken)
    {
        var steps = new List<PlanStep>();
        while (await reader.ReadAsync(cancellationToken))
        {
            steps.Add(new PlanStep(
                Int(reader, 0) ?? 0,
                Int(reader, 1),
                Int(reader, 2) ?? 0,
                Text(reader, 3) ?? "",
                Text(reader, 4),
                Text(reader, 5),
                Text(reader, 6),
                Long(reader, 7),
                Long(reader, 8),
                Long(reader, 9),
                Text(reader, 10),
                Text(reader, 11),
                actual ? Long(reader, 12) : null,
                actual ? Long(reader, 13) : null,
                actual && Long(reader, 14) is { } microseconds ? TimeSpan.FromMicroseconds(microseconds) : null,
                actual ? Long(reader, 15) : null));
        }

        return steps;
    }

    private static string? Text(DbDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static long? Long(DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : Convert.ToInt64(reader.GetValue(ordinal), CultureInfo.InvariantCulture);

    private static int? Int(DbDataReader reader, int ordinal) => Long(reader, ordinal) is { } value ? (int)value : null;
}
