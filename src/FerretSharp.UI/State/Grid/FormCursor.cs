using FerretSharp.Core.Data;
using FerretSharp.Core.Forms;
using FerretSharp.UI.Resources;

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

    /// <summary>The number of loaded rows once the last block was read; null while the end is not known.</summary>
    int? RowCount { get; }

    /// <summary>The key a row has now that had <paramref name="key"/> (its primary key was changed and written); the same otherwise.</summary>
    RowKey KeyNow(RowKey key) => key;

    /// <summary>Whether the tab's changes still hold <paramref name="change"/> (a commit or rollback cleared them).</summary>
    bool Tracks(RowChange change) => false;
}

/// <summary>What became of the shown row after the grid's rows changed (<see cref="FormCursor.RowsChanged"/>).</summary>
public enum FormFollow
{
    /// <summary>Same row, same place.</summary>
    Unchanged,

    /// <summary>Its block arrived: the row's key is known now (incoming FKs can be counted).</summary>
    KeyKnown,

    /// <summary>The row is somewhere else now (written, reloaded): move the grid's focus to <see cref="FormCursor.Cell"/>.</summary>
    Moved,

    /// <summary>
    /// The row is somewhere else with another key (inserted, or its primary key written): move the focus and count what points
    /// to it again – the incoming jumps still held the old values (E2E of `small-pk-edit`).
    /// </summary>
    MovedWithNewKey,

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

    /// <summary>
    /// The change of the shown loaded row, once it has one (WP-85): after a write the form follows its key, not the old one –
    /// another row may have taken the old key in the same write (a swap 1 → 9, 2 → 1 showed the other row).
    /// </summary>
    public RowChange? Change { get; private set; }

    /// <summary>The shown row as the grid held it: the same instance means the grid has not reloaded since.</summary>
    private RowData? _loaded;

    /// <summary>The grid's focus went to another row (or the form moved it there).</summary>
    public void Show(GridCell cell)
    {
        Cell = cell;
        Lost = null;
        var row = grid.RowOf(cell);
        Key = row?.Loaded?.Key;
        NewRow = cell.IsNew ? row?.Change : null;
        Change = cell.IsNew ? null : row?.Change;
        _loaded = row?.Loaded;
        PastTheEnd(cell);
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
                Change = added;
                Cell = new GridCell(index, null, cell.Column);
                _loaded = grid.RowAt(index)?.Loaded;
                return FormFollow.MovedWithNewKey;
            }

            if (!added.IsInserted || grid.RowAt(0) is not null)
            {
                return LoseRow(added.IsInserted
                    ? GridText.Form_InsertedRowOutside
                    : GridText.Form_NewRowRemoved);
            }

            return FormFollow.Unchanged; // inserted, the grid is still loading its first block
        }

        if (grid.RowAt(cell.RowIndex) is not { Loaded: { } loaded } current)
        {
            return PastTheEnd(cell) ? FormFollow.Lost : FormFollow.Unchanged; // its block is not loaded (yet)
        }

        if (Key is null)
        {
            Key = loaded.Key; // the block arrived after the row was chosen
            Track(current);
            return FormFollow.KeyKnown;
        }

        if (Change is { Key: RowKey.PrimaryKey moved } && !Equals(moved, Key))
        {
            return FollowChange(cell, loaded, moved);
        }

        if (Equals(loaded.Key, Key) && Change is { } mine && current.Change is { } other && !ReferenceEquals(mine, other) && grid.Tracks(mine))
        {
            // Another row holds the shown row's key now (it was deleted, another took the key in the same write; review of
            // WP-85): not the shown row – once the rows are read again. A row at another key there goes the usual way below
            // (sorted elsewhere: Moved).
            return ReferenceEquals(loaded, _loaded) ? FormFollow.Unchanged : LoseRow(GridText.Form_RowNotLoaded);
        }

        if (Key is RowKey.None || Equals(loaded.Key, Key))
        {
            Track(current);
            return FormFollow.Unchanged;
        }

        if (grid.IndexOf(Key) is { } at2)
        {
            Cell = cell with { RowIndex = at2 };
            Track(grid.RowAt(at2));
            return FormFollow.Moved;
        }

        // Not loaded under its key any more: its changed primary key may have been written, the row goes on with the new one
        // (review of `small-pk-edit`, 3). Only now – after a rollback the old key is the row's again (review, 11).
        if (grid.KeyNow(Key) is var now && !Equals(now, Key))
        {
            Key = now;
            return Found(cell, loaded) ?? LoseRow(GridText.Form_RowNotLoaded);
        }

        return LoseRow(GridText.Form_RowNotLoaded);
    }

    /// <summary>
    /// The shown row's change has another key than the form knows (WP-85): its changed primary key was written, or the write
    /// was taken back. Rows the grid still holds from before are not the answer – the reload after the write follows.
    /// </summary>
    private FormFollow FollowChange(GridCell cell, RowData loaded, RowKey moved)
    {
        if (ReferenceEquals(loaded, _loaded))
        {
            return FormFollow.Unchanged; // not reloaded yet: the old key may already be another row's
        }

        Key = moved;
        return Found(cell, loaded) ?? LoseRow(GridText.Form_RowNotLoaded);
    }

    /// <summary>The row under its new <see cref="Key"/>: in place or where the key sorts it to; null if it is not loaded.</summary>
    private FormFollow? Found(GridCell cell, RowData loaded)
    {
        if (Equals(loaded.Key, Key))
        {
            Track(grid.RowAt(cell.RowIndex));
            return FormFollow.KeyKnown; // in place with its new key: count what points to it
        }

        if (grid.IndexOf(Key!) is { } at)
        {
            Cell = cell with { RowIndex = at };
            Track(grid.RowAt(at));
            return FormFollow.MovedWithNewKey;
        }

        return null;
    }

    /// <summary>The shown row was found under its key: its change (one made since, or none after a commit) and its instance.</summary>
    private void Track(FormRow? row)
    {
        Change = row?.Change;
        _loaded = row?.Loaded;
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

    /// <summary>
    /// The grid ends before the shown index – an empty result, a filter with fewer rows (review 3.44.0, A10: the form said
    /// "loading row" for ever, waiting for a block that never comes).
    /// </summary>
    private bool PastTheEnd(GridCell cell)
    {
        if (cell.IsNew || grid.RowCount is not { } count || cell.RowIndex < count)
        {
            return false;
        }

        LoseRow(count == 0 ? GridText.Form_NoRows : GridText.Form_PastLastRow);
        return true;
    }

    private FormFollow LoseRow(string why)
    {
        Cell = null;
        NewRow = null;
        Change = null;
        _loaded = null;
        Lost = why;
        return FormFollow.Lost;
    }
}
