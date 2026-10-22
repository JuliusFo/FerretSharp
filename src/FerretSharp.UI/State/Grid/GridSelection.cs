using System.Globalization;
using FerretSharp.Core.Resources;
using FerretSharp.UI.Resources;

namespace FerretSharp.UI.State;

/// <summary>
/// The rows selected in a grid, or the cells or whole columns marked in it (WP-58), as grid.js reports them for the footer.
/// </summary>
/// <param name="Count">Selected rows; only loaded rows can be selected.</param>
/// <param name="ToEnd">Rows still to load are selected when they arrive (after Ctrl+A or a selection up to the last row).</param>
/// <param name="MarkedRows">Rows of the marked cells; 0 if no cells are marked.</param>
/// <param name="MarkedColumns">Columns of the marked cells, or marked whole columns.</param>
/// <param name="WholeColumns">Whole columns are marked: all rows of them, loaded or not.</param>
public sealed record GridSelection(int Count, bool ToEnd, int MarkedRows = 0, int MarkedColumns = 0, bool WholeColumns = false)
{
    public static readonly GridSelection None = new(0, false);

    /// <summary>
    /// The footer's text: the marked cells or columns, else the selected rows – null for no row or one (a click always selects
    /// one, saying so would be noise). <paramref name="allLoaded"/>: every row of the grid is loaded – then "loaded rows
    /// only" means nothing.
    /// </summary>
    public string? FooterText(bool allLoaded)
    {
        if (WholeColumns && MarkedColumns > 0)
        {
            return TextFormat.Plural(MarkedColumns, GridText.Footer_MarkedColumnsOne, GridText.Footer_MarkedColumnsOther);
        }

        if (MarkedRows > 0 && MarkedColumns > 0)
        {
            return MarkedRows == 1 && MarkedColumns == 1
                ? GridText.Footer_MarkedCell
                : TextFormat.Format(GridText.Footer_MarkedCells, Number(MarkedRows), Number(MarkedColumns));
        }

        return Count < 2
            ? null
            : TextFormat.Format(LoadedOnly(allLoaded) ? GridText.Footer_SelectedLoadedRows : GridText.Footer_SelectedRows, Number(Count));
    }

    /// <summary>Its tooltip: why only loaded rows count and what that means for deleting and copying; null if it does not apply.</summary>
    public string? FooterTitle(bool allLoaded) => MarkedColumns == 0 && Count >= 2 && LoadedOnly(allLoaded) ? GridText.Footer_SelectedLoadedRowsTitle : null;

    private bool LoadedOnly(bool allLoaded) => ToEnd && !allLoaded;

    private static string Number(int count) => count.ToString("N0", CultureInfo.CurrentCulture);
}

/// <summary>
/// What is marked in a grid (WP-58), as grid.js reports it with a key, a menu or a copy: a rectangle of cells, or whole
/// columns – all their rows, loaded or not.
/// </summary>
/// <param name="FromRow">First row of the cells (grid index); -1 for whole columns.</param>
/// <param name="ToRow">Last row of the cells; -1 for whole columns.</param>
/// <param name="Columns">Column ids (<c>c0</c>, <c>c1</c> …) in display order.</param>
/// <param name="Whole">Whole columns are marked.</param>
public sealed record GridMarks(int FromRow, int ToRow, string[] Columns, bool Whole)
{
    /// <summary>Rows of the cells; 0 for whole columns.</summary>
    public int Rows => Whole ? 0 : ToRow - FromRow + 1;

    /// <summary>The columns as indexes of the grid's columns, in display order.</summary>
    public IReadOnlyList<int> ColumnIndexes => [.. Columns.Select(GridColumn.IndexOf)];

    /// <summary>"3 × 4 cells", "2 columns · all rows" – headings and notices.</summary>
    public string Text => Whole
        ? TextFormat.Plural(Columns.Length, GridText.Marks_ColumnsOne, GridText.Marks_ColumnsOther)
        : Rows == 1 && Columns.Length == 1
            ? GridText.Marks_Cell
            : TextFormat.Format(GridText.Marks_Cells, Rows.ToString("N0", CultureInfo.CurrentCulture), Columns.Length.ToString("N0", CultureInfo.CurrentCulture));
}

/// <summary>A grid whose selection the footer shows (<c>FerretGrid</c>, <c>SqlResultGrid</c>).</summary>
public interface ISelectionGrid
{
    GridSelection Selection { get; }

    /// <summary>The selection changed; may be raised outside the renderer's thread.</summary>
    event Action? SelectionChanged;
}
