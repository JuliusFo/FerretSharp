namespace FerretSharp.UI.State;

/// <summary>Right-click on a column header.</summary>
/// <param name="Column">Index into <c>TableDetails.Columns</c>.</param>
/// <param name="X">Mouse position in CSS pixels, relative to the viewport.</param>
public sealed record ColumnMenuRequest(int Column, double X, double Y, double ViewportWidth, double ViewportHeight);
