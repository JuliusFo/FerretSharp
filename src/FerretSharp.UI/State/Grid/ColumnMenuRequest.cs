namespace FerretSharp.UI.State;

/// <summary>Right-click on a column header.</summary>
/// <param name="Column">Index into <c>TableDetails.Columns</c>.</param>
/// <param name="X">Mouse position in CSS pixels, relative to the viewport.</param>
/// <param name="Marked">
/// The whole columns marked with it (WP-58), in display order – the column menu adds them all to the same table elsewhere
/// (WP-61); null if the column is not marked as a whole.
/// </param>
public sealed record ColumnMenuRequest(int Column, double X, double Y, double ViewportWidth, double ViewportHeight, IReadOnlyList<int>? Marked = null);
