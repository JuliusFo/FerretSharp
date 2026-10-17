using FerretSharp.Core.Data;

namespace FerretSharp.UI.State;

/// <summary>Right-click on a grid cell.</summary>
/// <param name="Row">Raw values of the clicked row (not the display text).</param>
/// <param name="Column">Index into <c>TableDetails.Columns</c>.</param>
/// <param name="CellText">Display text of the cell; null for SQL NULL.</param>
/// <param name="Selected">Raw values of all selected rows in grid order (export).</param>
/// <param name="MissingRows">Selected rows whose block is no longer cached; they cannot be exported.</param>
/// <param name="X">Mouse position in CSS pixels, relative to the viewport.</param>
/// <param name="SelectedIndexes">Grid row indexes of the selected rows (delete/revert from the menu).</param>
public sealed record GridContextMenuRequest(
    RowData Row, int Column, string? CellText, IReadOnlyList<RowData> Selected, int MissingRows,
    double X, double Y, double ViewportWidth, double ViewportHeight, IReadOnlyList<int>? SelectedIndexes = null, int RowIndex = -1)
{
    /// <summary>
    /// FK jumps start from all selected rows (a right-click into a selection keeps it), else from the clicked row; rows no
    /// longer loaded are left out and mentioned below the jumps.
    /// </summary>
    public IReadOnlyList<RowData> JumpRows => Selected.Count > 0 ? Selected : [Row];

    /// <summary>" · 3 rows" after the jump headings if several rows are selected; nothing for one row.</summary>
    public string JumpRowsText => Selected.Count + MissingRows > 1 ? $" · {ExportActions.RowsText(JumpRows.Count)}" : "";
}
