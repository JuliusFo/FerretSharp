using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace FerretSharp.Core.Query;

/// <summary>Where a plan comes from (ADR 0012).</summary>
public enum PlanSource
{
    /// <summary><c>EXPLAIN PLAN</c>: the optimizer's plan without running the statement – and without bind values.</summary>
    Estimated,

    /// <summary>The cursor of a run with <c>GATHER_PLAN_STATISTICS</c>: the plan used, with actual rows, starts, time, buffers.</summary>
    Actual,
}

/// <summary>One operation of a plan, as <c>PLAN_TABLE</c> and <c>V$SQL_PLAN_STATISTICS_ALL</c> describe it.</summary>
/// <param name="Rows">Estimated rows per start (<c>CARDINALITY</c>).</param>
/// <param name="ActualRows">Rows this operation returned in the last run (all starts together); null for estimated plans.</param>
/// <param name="ActualTime">Time spent in this operation and below in the last run.</param>
public sealed record PlanStep(
    int Id,
    int? ParentId,
    int Depth,
    string Operation,
    string? Options,
    string? ObjectOwner,
    string? ObjectName,
    long? Cost,
    long? Rows,
    long? Bytes,
    string? AccessPredicates,
    string? FilterPredicates,
    long? ActualRows = null,
    long? Starts = null,
    TimeSpan? ActualTime = null,
    long? BufferGets = null)
{
    /// <summary><c>TABLE ACCESS FULL</c>, <c>INDEX RANGE SCAN</c> …</summary>
    public string Name => Options is null ? Operation : $"{Operation} {Options}";

    public bool IsFullTableScan => Operation == "TABLE ACCESS" && Options is "FULL" or "STORAGE FULL";

    /// <summary>All starts together, as the actual rows count them.</summary>
    public long? EstimatedTotal => Rows is { } rows ? rows * Math.Max(Starts ?? 1, 1) : null;
}

/// <param name="Sql">The statement as it was explained or run (with the statistics hint for actual plans).</param>
/// <param name="SqlId">The cursor's SQL_ID (actual plans).</param>
/// <param name="WholeResult">Actual plans: the whole result was fetched (otherwise only the first page).</param>
/// <param name="RowsFetched">Actual plans: rows fetched in the measured run.</param>
public sealed record ExecutionPlan(
    PlanSource Source,
    string Sql,
    IReadOnlyList<PlanStep> Steps,
    string? SqlId = null,
    int? ChildNumber = null,
    bool WholeResult = false,
    long? RowsFetched = null)
{
    /// <summary>Estimates are this far off (either way) before a step is marked.</summary>
    public const int MisestimateFactor = 10;

    /// <summary>Below this many rows a wrong estimate does not matter.</summary>
    public const int MisestimateMinimumRows = 100;

    /// <summary>
    /// The optimizer misjudged the rows of an actual run by <see cref="MisestimateFactor"/> or more – the most common reason
    /// for a bad plan. Without the whole result only underestimates count: fetching stopped early, so fewer rows are normal.
    /// </summary>
    public bool IsMisestimate(PlanStep step)
    {
        if (Source != PlanSource.Actual || step.ActualRows is not { } actual || step.EstimatedTotal is not { } estimated)
        {
            return false;
        }

        if (Math.Max(actual, estimated) < MisestimateMinimumRows)
        {
            return false;
        }

        return actual >= estimated * MisestimateFactor || (WholeResult && estimated >= Math.Max(actual, 1) * MisestimateFactor);
    }
}

/// <summary>Helpers around plans: the cursor's SQL_ID, the statistics hint, the text form.</summary>
public static partial class Plans
{
    public const string StatisticsHint = "GATHER_PLAN_STATISTICS";

    /// <summary>
    /// Oracle's SQL_ID of a statement text, computed here so the cursor can be found without asking <c>V$SESSION</c>: the
    /// last 8 bytes of MD5(text + NUL) as two little-endian 32-bit halves, written in base 32. The text must be exactly
    /// what was sent.
    /// </summary>
    public static string SqlId(string sql)
    {
        var md5 = MD5.HashData([.. Encoding.UTF8.GetBytes(sql), 0]);
        var value = ((ulong)BitConverter.ToUInt32(md5, 8) << 32) | BitConverter.ToUInt32(md5, 12);
        const string alphabet = "0123456789abcdfghjkmnpqrstuvwxyz";
        var id = new char[13];
        for (var i = id.Length - 1; i >= 0; i--)
        {
            id[i] = alphabet[(int)(value & 31)];
            value >>= 5;
        }

        return new string(id);
    }

    /// <summary>
    /// The statement with <c>GATHER_PLAN_STATISTICS</c> after its first SELECT. Oracle reads only the first hint comment
    /// there, so an existing one gets the hint added instead of a second comment that would silently drop it.
    /// </summary>
    public static string WithStatistics(string sql)
    {
        var match = FirstSelect().Match(sql);
        if (!match.Success)
        {
            return sql;
        }

        var after = match.Index + match.Length;
        var existing = ExistingHint().Match(sql, after);
        return existing.Success && existing.Index == after
            ? sql[..(existing.Index + existing.Length)] + " " + StatisticsHint + sql[(existing.Index + existing.Length)..]
            : sql[..after] + " /*+ " + StatisticsHint + " */" + sql[after..];
    }

    /// <summary>A plan as text in the style of <c>DBMS_XPLAN</c>, with the predicates below – to paste into a ticket or chat.</summary>
    public static string Format(ExecutionPlan plan)
    {
        var actual = plan.Source == PlanSource.Actual;
        var header = actual
            ? new[] { "Id", "Operation", "Name", "Starts", "E-Rows", "A-Rows", "A-Time", "Buffers", "Cost" }
            : ["Id", "Operation", "Name", "Rows", "Bytes", "Cost"];
        var rows = plan.Steps.Select(s =>
        {
            var marker = s.AccessPredicates is not null || s.FilterPredicates is not null ? "*" : " ";
            var id = marker + s.Id.ToString(CultureInfo.InvariantCulture);
            var operation = new string(' ', s.Depth) + s.Name;
            var name = s.ObjectName ?? "";
            return actual
                ? new[] { id, operation, name, Number(s.Starts), Number(s.Rows), Number(s.ActualRows), Time(s.ActualTime), Number(s.BufferGets), Number(s.Cost) }
                : [id, operation, name, Number(s.Rows), Number(s.Bytes), Number(s.Cost)];
        }).ToList();

        var widths = header.Select((h, i) => rows.Select(r => r[i].Length).Append(h.Length).Max()).ToArray();
        var line = "-" + string.Concat(widths.Select(w => new string('-', w + 3)));
        var text = new StringBuilder();
        if (plan.SqlId is { } sqlId)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"SQL_ID {sqlId}, child number {plan.ChildNumber}");
        }

        text.AppendLine(line);
        text.AppendLine(Row(header, widths, numeric: _ => false));
        text.AppendLine(line);
        foreach (var row in rows)
        {
            text.AppendLine(Row(row, widths, numeric: i => i != 1 && i != 2));
        }

        text.AppendLine(line);
        var predicates = plan.Steps.Where(s => s.AccessPredicates is not null || s.FilterPredicates is not null).ToList();
        if (predicates.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("Predicate Information (identified by operation id):");
            text.AppendLine("---------------------------------------------------");
            foreach (var step in predicates)
            {
                var id = step.Id.ToString(CultureInfo.InvariantCulture).PadLeft(4);
                if (step.AccessPredicates is { } access)
                {
                    text.AppendLine(CultureInfo.InvariantCulture, $"{id} - access({access})");
                    id = "    ";
                }

                if (step.FilterPredicates is { } filter)
                {
                    text.AppendLine(CultureInfo.InvariantCulture, $"{id} - filter({filter})");
                }
            }
        }

        return text.ToString().TrimEnd();
    }

    private static string Row(IReadOnlyList<string> cells, int[] widths, Func<int, bool> numeric) =>
        "|" + string.Concat(cells.Select((c, i) => " " + (numeric(i) ? c.PadLeft(widths[i]) : c.PadRight(widths[i])) + " |"));

    /// <summary>Like DBMS_XPLAN: 1234, 12K, 3M.</summary>
    internal static string Number(long? value) => value switch
    {
        null => "",
        < 100_000 => value.Value.ToString(CultureInfo.InvariantCulture),
        < 100_000_000 => (value.Value / 1000).ToString(CultureInfo.InvariantCulture) + "K",
        _ => (value.Value / 1_000_000).ToString(CultureInfo.InvariantCulture) + "M",
    };

    private static string Time(TimeSpan? time) => time is { } t ? t.ToString(@"hh\:mm\:ss\.ff", CultureInfo.InvariantCulture) : "";

    [GeneratedRegex(@"\bSELECT\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FirstSelect();

    [GeneratedRegex(@"\s*/\*\+", RegexOptions.CultureInvariant)]
    private static partial Regex ExistingHint();
}

/// <summary>A plan that cannot be read here, with the reason for the user (missing V$ rights, a locked session).</summary>
public sealed class PlanUnavailableException(string message, Exception? inner = null) : Connections.RefusedException(message, inner);
