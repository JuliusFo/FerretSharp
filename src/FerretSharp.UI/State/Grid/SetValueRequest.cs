using FerretSharp.Core.Data;

namespace FerretSharp.UI.State;

/// <summary>
/// "Set value…" of the cell menu (WP-59): one value for the marked cells, or for one column of the selected rows. The
/// dialog's field is the form's (WP-21) for <paramref name="Column"/>, starting with the clicked row's value as shown.
/// </summary>
/// <param name="Row">The clicked row, pending values applied (A21).</param>
/// <param name="Column">The column whose editor the dialog shows: the clicked one, or the first marked one that can be edited.</param>
/// <param name="Marks">The marked cells or columns; null for the selected rows.</param>
/// <param name="Rows">The grid indexes of the selected rows (without <paramref name="Marks"/>).</param>
/// <param name="Target">What the value goes to, for the dialog's header ("3 × 4 cells", "NAME · 12 rows").</param>
public sealed record SetValueRequest(RowData Row, int Column, GridMarks? Marks, IReadOnlyList<int> Rows, string Target);
