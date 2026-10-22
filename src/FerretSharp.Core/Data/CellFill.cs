namespace FerretSharp.Core.Data;

/// <summary>What a command puts into marked cells (WP-59).</summary>
public enum CellFillKind
{
    /// <summary>One typed value, which each column parses itself (Ctrl+Enter in a cell editor, "Set value…").</summary>
    Value,

    /// <summary>NULL (Del on marked cells).</summary>
    Null,

    /// <summary>The top row's values into the rows below (Ctrl+D).</summary>
    Down,
}

/// <summary>A command for many cells at once (<see cref="ChangeTracker.SetCells"/>).</summary>
/// <param name="Text">The typed value of <see cref="CellFillKind.Value"/>: the editor's text, an enum member's database value.</param>
public sealed record CellFill(CellFillKind Kind, string Text = "")
{
    public static CellFill Null { get; } = new(CellFillKind.Null);

    public static CellFill Down { get; } = new(CellFillKind.Down);

    public static CellFill Value(string text) => new(CellFillKind.Value, text);
}

/// <summary>A column a command for many cells left as it is, and why.</summary>
public sealed record SkippedColumn(int Column, string Name, string Reason);

/// <summary>What a command for many cells did (<see cref="ChangeTracker.SetCells"/>).</summary>
/// <param name="Cells">Cells set, also those set back to the value the database holds.</param>
/// <param name="Columns">Columns left as they are: not editable, NOT NULL for NULL, a top value that cannot go down.</param>
/// <param name="DeletedRows">Rows left out because they are marked for deletion.</param>
/// <param name="TooPrecise">Cells left out because they hold a number too precise to edit here.</param>
/// <param name="Error">Why nothing was set (a value invalid for one of the columns, too many rows); null if the command ran.</param>
public sealed record CellFillResult(int Cells, IReadOnlyList<SkippedColumn> Columns, int DeletedRows, int TooPrecise, string? Error)
{
    /// <summary>The rows the command set a cell of, top to bottom: only they need drawing again and their names looked up.</summary>
    public IReadOnlyList<RowKey> Changed { get; init; } = [];

    internal static CellFillResult Failed(string error) => new(0, [], 0, 0, error);
}
