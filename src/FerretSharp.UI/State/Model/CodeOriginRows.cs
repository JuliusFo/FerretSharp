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
    public static string Reasons(CodeOriginCandidate candidate) => string.Join(" · ", candidate.Reasons.Select(Reason));

    public static string Reason(CodeOriginReason reason) => reason.Kind switch
    {
        CodeOriginReasonKind.Queries => TextFormat.Format(ModelViewText.CodeOrigin_Queries, reason.Table),
        CodeOriginReasonKind.Joins => TextFormat.Format(ModelViewText.CodeOrigin_Joins, reason.Table, reason.Navigation),
        CodeOriginReasonKind.LazyLoads => TextFormat.Format(ModelViewText.CodeOrigin_LazyLoads, reason.Table, reason.Navigation),
        CodeOriginReasonKind.Filters => TextFormat.Format(ModelViewText.CodeOrigin_Filters, Columns(reason.Columns)),
        CodeOriginReasonKind.Writes => TextFormat.Format(ModelViewText.CodeOrigin_Writes, reason.Table, Columns(reason.Columns)),
        _ => reason.Table,
    };

    private static string Columns(IReadOnlyList<string> columns) =>
        string.Join(", ", columns.Take(MaxColumns)) + (columns.Count > MaxColumns ? ", …" : "");
}
