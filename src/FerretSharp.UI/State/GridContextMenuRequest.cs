using FerretSharp.Core.Data;

namespace FerretSharp.UI.State;

/// <summary>Right-click on a grid cell.</summary>
/// <param name="Row">Raw values of the clicked row (not the display text).</param>
/// <param name="Column">Index into <c>TableDetails.Columns</c>.</param>
/// <param name="CellText">Display text of the cell; null for SQL NULL.</param>
/// <param name="Selected">Raw values of all selected rows in grid order (export).</param>
/// <param name="MissingRows">Selected rows whose block is no longer cached; they cannot be exported.</param>
/// <param name="X">Mouse position in CSS pixels, relative to the viewport.</param>
public sealed record GridContextMenuRequest(
    RowData Row, int Column, string? CellText, IReadOnlyList<RowData> Selected, int MissingRows,
    double X, double Y, double ViewportWidth, double ViewportHeight);
