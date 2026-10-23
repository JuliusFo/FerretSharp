namespace FerretSharp.UI.State;

/// <summary>One field of the export matrix of a right-click menu (WP-61): a format copied or saved.</summary>
/// <param name="Label">What it does ("Save as CSV…"): first line of the tooltip, found by the menu's filter.</param>
/// <param name="Title">What it gives: second line of the tooltip.</param>
/// <param name="Unavailable">Why it is not possible here (disabled, in place of <paramref name="Title"/>); null if it is.</param>
public sealed record ExportMatrixCell(string Label, string Title, Func<Task> Run, string? Unavailable = null)
{
    public string Tooltip => Label + Environment.NewLine + (Unavailable ?? Title);
}

/// <summary>
/// A format of the export matrix (WP-61) with its fields: copy it, save the selected rows, save all rows of the grid's query.
/// A null field is a combination there is not (a dash).
/// </summary>
public sealed record ExportMatrixRow(string Format, ExportMatrixCell? Copy, ExportMatrixCell? Selection = null, ExportMatrixCell? All = null)
{
    /// <summary>The row stays while the filter matches its format or one of its fields.</summary>
    public bool Matches(MenuFilter filter) => filter.Matches(Format, Copy?.Label, Selection?.Label, All?.Label);
}
