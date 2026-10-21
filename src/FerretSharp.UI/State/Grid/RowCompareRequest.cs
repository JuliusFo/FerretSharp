using FerretSharp.Core.Forms;
using FerretSharp.Core.Resources;
using FerretSharp.UI.Resources;

namespace FerretSharp.UI.State;

/// <summary>
/// The rows "Compare selected" shows side by side (WP-21; query results since WP-56). One rule for the table's and the
/// result's grid (review 3.44.0, B1: it stood in both, word for word).
/// </summary>
/// <param name="Rows">The selected rows still loaded, with their grid indexes.</param>
/// <param name="Missing">Selected rows no longer loaded: counted, not compared.</param>
public sealed record RowCompareRequest(IReadOnlyList<(int Index, FormRow Row)> Rows, int Missing)
{
    /// <summary>The loaded rows of the selection; null and the reason if they cannot be compared.</summary>
    /// <param name="rowAt">A loaded row by grid index; null if its block is not loaded.</param>
    public static (RowCompareRequest? Request, string? Refusal) Of(IReadOnlyList<int> selected, Func<int, FormRow?> rowAt)
    {
        if (selected.Count > RowComparison.MaxRows)
        {
            return (null, TextFormat.Format(SchemaViewText.Tab_CompareTooMany, RowComparison.MaxRows, selected.Count));
        }

        var rows = selected.Select(i => (Index: i, Row: rowAt(i))).Where(r => r.Row is not null).Select(r => (r.Index, r.Row!)).ToList();
        return rows.Count < 2
            ? (null, SchemaViewText.Tab_CompareTooFew)
            : (new RowCompareRequest(rows, selected.Count - rows.Count), null);
    }
}
