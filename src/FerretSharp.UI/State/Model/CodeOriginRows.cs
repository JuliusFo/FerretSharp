using FerretSharp.Core.ClrModel;
using FerretSharp.Core.Resources;
using FerretSharp.UI.Resources;

namespace FerretSharp.UI.State;

/// <summary>How the candidates for a statement's origin in code read (WP-76).</summary>
public static class CodeOriginRows
{
    /// <summary>Candidates shown before "N more" (proposal, not confirmed).</summary>
    public const int Initial = 5;

    /// <summary>Columns named per reason; more end in "…".</summary>
    private const int MaxColumns = 4;

    /// <summary>The candidates to show: the first <see cref="Initial"/>, all when expanded.</summary>
    public static IReadOnlyList<CodeOriginCandidate> Shown(IReadOnlyList<CodeOriginCandidate> candidates, bool expanded) =>
        expanded ? candidates : candidates.Take(Initial).ToList();

    /// <summary>Why the method fits, one phrase per reason: "queries ORDERS · filters by CUSTOMER_ID, STATUS".</summary>
    /// <param name="qualify">The statement has several tables: filters name theirs ("filters ORDERS by STATUS").</param>
    public static string Reasons(CodeOriginCandidate candidate, bool qualify = false) =>
        string.Join(" · ", candidate.Reasons.Select(r => Reason(r, qualify)));

    public static string Reason(CodeOriginReason reason, bool qualify = false) => reason.Kind switch
    {
        CodeOriginReasonKind.Queries => TextFormat.Format(ModelViewText.CodeOrigin_Queries, reason.Table),
        CodeOriginReasonKind.Joins => TextFormat.Format(ModelViewText.CodeOrigin_Joins, reason.Table, reason.Navigation),
        CodeOriginReasonKind.LazyLoads => TextFormat.Format(ModelViewText.CodeOrigin_LazyLoads, reason.Table, reason.Navigation),
        CodeOriginReasonKind.Filters when qualify => TextFormat.Format(ModelViewText.CodeOrigin_FiltersIn, reason.Table, Columns(reason.Columns)),
        CodeOriginReasonKind.Filters => TextFormat.Format(ModelViewText.CodeOrigin_Filters, Columns(reason.Columns)),
        CodeOriginReasonKind.Writes => TextFormat.Format(ModelViewText.CodeOrigin_Writes, reason.Table, Columns(reason.Columns)),
        _ => reason.Table,
    };

    /// <summary>Segments of the fit meter (proposal, not confirmed).</summary>
    public const int Segments = 5;

    /// <summary>
    /// The rank of each candidate, by score: candidates with the same score share the rank of the first of them (1, 2, 2, 4)
    /// and are marked as level – the score does not decide between them.
    /// </summary>
    public static IReadOnlyList<(int Rank, bool Level)> Ranks(IReadOnlyList<CodeOriginCandidate> candidates)
    {
        var ranks = new List<(int, bool)>(candidates.Count);
        for (var i = 0; i < candidates.Count; i++)
        {
            var first = i;
            while (first > 0 && candidates[first - 1].Score == candidates[i].Score)
            {
                first--;
            }

            var level = first < i || (i + 1 < candidates.Count && candidates[i + 1].Score == candidates[i].Score);
            ranks.Add((first + 1, level));
        }

        return ranks;
    }

    /// <summary>
    /// The filled segments of the meter: all only for a full fit, at least one – a candidate always covers something of the
    /// statement.
    /// </summary>
    public static int Filled(CodeOriginFit fit) =>
        fit.Matched >= fit.Total ? Segments : Math.Clamp((int)Math.Round(fit.Share * Segments, MidpointRounding.AwayFromZero), 1, Segments - 1);

    /// <summary>
    /// The meter's colour: <c>full</c> only for a method that covers the whole statement and filters or joins nothing beyond
    /// it (E2E: one with an extra filter ranked third, as green as the first), <c>half</c> for at least half covered, else
    /// <c>low</c>.
    /// </summary>
    public static string Tone(CodeOriginFit fit) =>
        fit.Matched >= fit.Total && fit.Extra.Count == 0 ? "full" : fit.Share >= 0.5 ? "half" : "low";

    /// <summary>The meter's tooltip: how much it covers, what is missing, what the method has beyond the statement – a line each.</summary>
    public static string FitTitle(CodeOriginFit fit, bool qualify = false)
    {
        var lines = new List<string> { TextFormat.Format(ModelViewText.CodeOrigin_Fit, fit.Matched, fit.Total) };
        if (fit.Missing.Count > 0)
        {
            lines.Add(TextFormat.Format(ModelViewText.CodeOrigin_FitMissing, string.Join(" · ", fit.Missing.Select(r => Reason(r, qualify)))));
        }

        if (fit.Extra.Count > 0)
        {
            lines.Add(TextFormat.Format(ModelViewText.CodeOrigin_FitExtra, Columns(fit.Extra)));
        }

        return string.Join('\n', lines);
    }

    private static string Columns(IReadOnlyList<string> columns) =>
        string.Join(", ", columns.Take(MaxColumns)) + (columns.Count > MaxColumns ? ", …" : "");
}
