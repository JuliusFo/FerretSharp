using FerretSharp.Core.Data;
using FerretSharp.Core.Forms;

namespace FerretSharp.UI.State;

/// <summary>A grid position: a loaded row by index, or a new row (pinned at the top) by its id.</summary>
/// <param name="Column">The focused column; null if unknown.</param>
public sealed record GridCell(int RowIndex, string? NewId, int? Column)
{
    public bool IsNew => NewId is not null;
}

/// <summary>What the form needs to know of the grid's rows (implemented by <c>FerretGrid</c>; a fake in tests).</summary>
public interface IFormGrid
{
    /// <summary>The row at a grid position with the tab's changes; null if its block is not (or no longer) loaded.</summary>
    FormRow? RowOf(GridCell cell);

    /// <summary>A loaded row with the tab's changes; null if its block is not (or no longer) loaded.</summary>
    FormRow? RowAt(int rowIndex);

    /// <summary>Grid index of a loaded row by its key.</summary>
    int? IndexOf(RowKey key);

    /// <summary>The grid position of a new row that is not inserted yet; null if it is gone (inserted or removed).</summary>
    GridCell? CellOf(RowChange newRow, int? column);
}

/// <summary>What became of the shown row after the grid's rows changed (<see cref="FormCursor.RowsChanged"/>).</summary>
public enum FormFollow
{
    /// <summary>Same row, same place.</summary>
    Unchanged,

    /// <summary>Its block arrived: the row's key is known now (incoming FKs can be counted).</summary>
    KeyKnown,

    /// <summary>The row is somewhere else now (written, inserted): move the grid's focus to <see cref="FormCursor.Cell"/>.</summary>
    Moved,

    /// <summary>The row is gone from the grid; <see cref="FormCursor.Lost"/> says why.</summary>
    Lost,
}

/// <summary>
/// The row the form shows, followed through the grid (WP-21; R3b out of <c>RowFormPanel</c>, now with tests). The grid
/// leads: the form shows the row of the focused cell. After writing or reloading the row may move (found again by its
/// key), a new row becomes a loaded one once inserted, and a row can drop out of the loaded range.
/// </summary>
public sealed class FormCursor(IFormGrid grid)
{
    /// <summary>The shown position; null when nothing is shown.</summary>
    public GridCell? Cell { get; private set; }

    /// <summary>The key of the shown loaded row; null until its block is loaded.</summary>
    public RowKey? Key { get; private set; }

    /// <summary>The shown new row (not inserted yet).</summary>
    public RowChange? NewRow { get; private set; }

    /// <summary>Why nothing is shown any more; null otherwise.</summary>
    public string? Lost { get; private set; }

    /// <summary>The grid's focus went to another row (or the form moved it there).</summary>
    public void Show(GridCell cell)
    {
        Cell = cell;
        Lost = null;
        var row = grid.RowOf(cell);
        Key = row?.Loaded?.Key;
        NewRow = cell.IsNew ? row?.Change : null;
    }

    /// <summary>The focused cell is in the shown row (only the column changed).</summary>
    public bool Shows(GridCell cell) =>
        Cell is { } shown && shown.NewId == cell.NewId && (cell.IsNew || shown.RowIndex == cell.RowIndex);

    /// <summary>Rows were loaded or redrawn: the shown row may have moved (after writing), become loaded, or be gone.</summary>
    public FormFollow RowsChanged()
    {
        if (Cell is not { } cell)
        {
            return FormFollow.Unchanged;
        }

        if (NewRow is { } added)
        {
            if (grid.CellOf(added, cell.Column) is { } at)
            {
                Cell = at; // still pending at the top, where the grid's focus already is
                return FormFollow.Unchanged;
            }

            if (added.IsInserted && grid.IndexOf(added.Key) is { } index)
            {
                // Inserted by writing: from now on a loaded row like any other.
                NewRow = null;
                Key = added.Key;
                Cell = new GridCell(index, null, cell.Column);
                return FormFollow.Moved;
            }

            if (!added.IsInserted || grid.RowAt(0) is not null)
            {
                return LoseRow(added.IsInserted
                    ? "Die eingefügte Zeile steht nicht im geladenen Bereich des Grids – im Grid eine Zeile wählen."
                    : "Die neue Zeile wurde entfernt.");
            }

            return FormFollow.Unchanged; // inserted, the grid is still loading its first block
        }

        if (grid.RowAt(cell.RowIndex) is not { Loaded: { } loaded })
        {
            return FormFollow.Unchanged; // its block is not loaded (yet)
        }

        if (Key is null)
        {
            Key = loaded.Key; // the block arrived after the row was chosen
            return FormFollow.KeyKnown;
        }

        if (Key is RowKey.None || Equals(loaded.Key, Key))
        {
            return FormFollow.Unchanged;
        }

        if (grid.IndexOf(Key) is { } at2)
        {
            Cell = cell with { RowIndex = at2 };
            return FormFollow.Moved;
        }

        return LoseRow("Die Zeile ist nicht mehr geladen – im Grid eine Zeile wählen.");
    }

    /// <summary>
    /// Where ▲ ▼ lead: the new rows at the top first, then the loaded rows; null at either end. <paramref name="lastIndex"/>
    /// is the last row index the grid can have (all loaded, counted, or unknown = <see cref="int.MaxValue"/>).
    /// </summary>
    public GridCell? Target(int delta, IReadOnlyList<RowChange> newRows, int lastIndex)
    {
        if (Cell is not { } cell)
        {
            return null;
        }

        if (cell.IsNew)
        {
            var i = newRows.ToList().FindIndex(r => GridRows.NewRowId(r) == cell.NewId) + delta;
            return i < 0 ? null
                : i < newRows.Count ? grid.CellOf(newRows[i], cell.Column)
                : new GridCell(0, null, cell.Column);
        }

        var target = cell.RowIndex + delta;
        if (target < 0)
        {
            return newRows.Count > 0 ? grid.CellOf(newRows[^1], cell.Column) : null;
        }

        return target > lastIndex ? null : new GridCell(target, null, cell.Column);
    }

    private FormFollow LoseRow(string why)
    {
        Cell = null;
        NewRow = null;
        Lost = why;
        return FormFollow.Lost;
    }
}
