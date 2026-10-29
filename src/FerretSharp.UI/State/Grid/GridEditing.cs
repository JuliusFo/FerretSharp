using FerretSharp.Core.Data;
using FerretSharp.Core.Oracle;
using FerretSharp.Core.Resources;
using FerretSharp.Core.Schema;
using FerretSharp.UI.Resources;

namespace FerretSharp.UI.State;

/// <summary>What <see cref="GridEditing"/> needs of the table grid (<c>FerretGrid</c> implements it).</summary>
public interface IEditableGrid
{
    /// <summary>The tab's changes; null while the table cannot be edited.</summary>
    ChangeTracker? Changes { get; }

    /// <summary>
    /// The workspace writes or commits: an edit now could be marked written without being sent, or be cleared by the
    /// commit (review 3.31.0, A3). Edits are refused meanwhile.
    /// </summary>
    bool WriteRunning { get; }

    /// <summary>A loaded row by its grid index; null if its block is not (or no longer) loaded.</summary>
    RowData? RowAt(int rowIndex);

    /// <summary>The grid row of a loaded or new row, with its pending and written values (<see cref="GridRows.Build"/>).</summary>
    Dictionary<string, object?> Draw(RowChange? change, IReadOnlyList<object?>? values);

    Task UpdateRowsAsync(List<GridRowUpdate> updates);

    /// <summary>Sets the new (not yet inserted) rows pinned at the top again.</summary>
    Task SetNewRowsAsync();

    /// <summary>A foreign key typed in: its display name, or "not found" (WP-31).</summary>
    void QueueDisplayNames(IReadOnlyList<object?> values);

    /// <summary>The tab's changes changed: status bar, tab header and form follow.</summary>
    void ChangesMade();

    void Notify(string text);

    /// <summary>"Row no longer loaded – F5 loads it again."</summary>
    string RowNotLoaded { get; }

    /// <summary>How many rows the query has, once all of them are loaded; null while more may follow (WP-59: whole columns).</summary>
    int? AllRows { get; }

    /// <summary>
    /// Rows not loaded, read from a grid index on (start, count) as the database has them; null if the read failed (said why)
    /// or was cancelled – the grid cancels when it goes away (review 3.54.0, A5).
    /// </summary>
    Task<IReadOnlyList<RowData>?> ReadRowsAsync(int start, int count, CancellationToken cancellationToken);
}

/// <summary>A command for many cells (WP-59): the loaded rows to redraw, or why nothing was set.</summary>
/// <param name="Error">Why nothing was set; empty if that was said already (a read failed); null if the command ran.</param>
public sealed record GridFill(GridRowUpdate[] Rows, string? Error)
{
    public static GridFill Refused(string error) => new([], error);
}

/// <param name="Row">The new row.</param>
/// <param name="Column">The first column that can be typed into; null if none.</param>
/// <param name="PinnedIndex">The row's index among the new rows pinned at the top.</param>
public sealed record AddedRow(RowChange Row, int? Column, int PinnedIndex);

/// <summary>
/// Editing in the table grid (v2, WP-09): typed values, Del, the cell menu's delete and revert, a new row, a LOB's new
/// content – each kept as a pending change of the tab, refused while the workspace writes. R6b (review 3.40.0, B10): out
/// of <c>FerretGrid</c>, which passes JS calls on and draws.
/// </summary>
public sealed class GridEditing(IEditableGrid grid, TableDetails details)
{
    /// <summary>True (and says so) while the workspace writes: the caller changes nothing.</summary>
    public bool StoppedByWrite()
    {
        if (!grid.WriteRunning)
        {
            return false;
        }

        grid.Notify(GridText.Grid_WriteRunning);
        return true;
    }

    /// <summary>A typed value for a cell of a loaded or new row: checked and kept as a pending change.</summary>
    public GridEdit Edit(int rowIndex, string? newId, int column, string text)
    {
        if (grid.Changes is not { } changes)
        {
            return new GridEdit(null, GridText.Grid_TableNotEditable);
        }

        if (grid.WriteRunning)
        {
            return new GridEdit(null, GridText.Grid_WriteRunning);
        }

        EditResult result;
        if (GridRows.NewRow(changes, newId) is { } added)
        {
            result = changes.SetValue(added, column, text);
            grid.ChangesMade();
            if (result.IsValid)
            {
                grid.QueueDisplayNames(ValuesOf(added));
            }

            return new GridEdit(result.IsValid ? grid.Draw(added, null) : null, result.Error);
        }

        if (newId is not null || grid.RowAt(rowIndex) is not { } row)
        {
            return new GridEdit(null, grid.RowNotLoaded);
        }

        result = changes.SetValue(row, column, text);
        grid.ChangesMade();
        if (result.IsValid)
        {
            grid.QueueDisplayNames(changes.Find(row.Key) is { } change ? ValuesOf(change) : row.Values);
        }

        return new GridEdit(result.IsValid ? Draw(changes, row) : null, result.Error);
    }

    /// <summary>The LOB editor's result: the whole new value; <paramref name="loaded"/> is what it read from the database.</summary>
    /// <param name="row">The loaded row and its grid index, or null for <paramref name="newRow"/>.</param>
    /// <returns>Why the value was not taken; null if it was.</returns>
    public async Task<string?> SetContentAsync((int Index, RowData Row)? row, RowChange? newRow, int column, object? loaded, object? value)
    {
        if (grid.Changes is not { } changes)
        {
            return GridText.Grid_TableNotEditable;
        }

        if (grid.WriteRunning)
        {
            return GridText.Grid_WriteRunning;
        }

        EditResult result;
        if (newRow is not null)
        {
            result = changes.SetContent(newRow, column, value);
            if (result.IsValid)
            {
                await grid.SetNewRowsAsync();
            }
        }
        else
        {
            var (index, loadedRow) = row!.Value;
            result = changes.SetContent(loadedRow, column, loaded, value);
            if (result.IsValid)
            {
                await grid.UpdateRowsAsync([new(index, Draw(changes, loadedRow))]);
            }
        }

        grid.ChangesMade();
        return result.Error;
    }

    /// <summary>
    /// Del in the grid: the new row with the focus disappears, otherwise the selected loaded rows are marked for deletion.
    /// New rows are pinned and never selected, so the last clicked loaded row stays selected while a new row has the
    /// focus – it must not go with it.
    /// </summary>
    /// <param name="rowIndexes">The selected loaded rows.</param>
    /// <param name="focusedNewId">The new row with the focus, or null.</param>
    /// <returns>The loaded rows to redraw (grid.js draws them itself).</returns>
    public async Task<GridRowUpdate[]> DeleteKeyAsync(IReadOnlyList<int> rowIndexes, string? focusedNewId)
    {
        if (grid.Changes is not { } changes || StoppedByWrite())
        {
            return [];
        }

        if (focusedNewId is not null)
        {
            if (GridRows.NewRow(changes, focusedNewId) is { } added)
            {
                changes.Delete(added);
                await grid.SetNewRowsAsync();
                grid.ChangesMade();
            }

            return [];
        }

        var rows = Loaded(rowIndexes);
        changes.Delete([.. rows.Select(r => r.Row)]);
        grid.ChangesMade();
        return [.. rows.Select(r => new GridRowUpdate(r.Index, Draw(changes, r.Row)))];
    }

    /// <summary>Deletes or reverts loaded rows from the cell menu; redraws them.</summary>
    public async Task ChangeRowsAsync(IReadOnlyList<int> rowIndexes, bool delete)
    {
        if (grid.Changes is not { } changes || StoppedByWrite())
        {
            return;
        }

        var rows = Loaded(rowIndexes);
        if (delete)
        {
            changes.Delete([.. rows.Select(r => r.Row)]);
        }
        else
        {
            changes.Revert([.. rows.Select(r => r.Row.Key)]);
        }

        await grid.UpdateRowsAsync([.. rows.Select(r => new GridRowUpdate(r.Index, Draw(changes, r.Row)))]);
        grid.ChangesMade();
    }

    /// <summary>An empty new row at the top; null if the table cannot be edited now. The caller focuses or edits it.</summary>
    /// <param name="displayOrder">The columns in the order the grid shows them (pinned first).</param>
    public async Task<AddedRow?> AddRowAsync(IEnumerable<int> displayOrder)
    {
        if (grid.Changes is not { } changes || StoppedByWrite())
        {
            return null;
        }

        var added = changes.AddRow();
        await grid.SetNewRowsAsync();
        var first = displayOrder.FirstOrDefault(i => OracleTypeMapper.NotEditableReason(details, details.Columns[i], newRow: true) is null, -1);
        grid.ChangesMade();
        return new AddedRow(added, first >= 0 ? first : null, changes.NewRows.Count - 1);
    }

    /// <summary>
    /// A command for marked cells or whole columns (WP-59): Ctrl+Enter in a cell editor, Del, Ctrl+D, the cell menu. A range's
    /// rows not loaded are read first; whole columns only once all rows of the query are loaded (a set-based UPDATE of rows
    /// never seen is not offered). What was set and left out goes to the status bar.
    /// </summary>
    public async Task<GridFill> FillAsync(GridMarks marks, CellFill fill)
    {
        if (marks.Whole && grid.AllRows is null)
        {
            return GridFill.Refused(GridText.Fill_ColumnsNotLoaded);
        }

        var (from, to) = marks.Whole ? (0, grid.AllRows!.Value - 1) : (marks.FromRow, marks.ToRow);
        if (Refusal(to - from + 1, fill) is { } refused)
        {
            return refused;
        }

        var changes = grid.Changes;
        if (await CellCopies.RowsAsync(grid.RowAt, grid.ReadRowsAsync, from, to, CancellationToken.None) is not { } rows)
        {
            return GridFill.Refused(""); // the read said why
        }

        // Reading rows over the VPN takes a moment: a write may have started meanwhile, or the tab dropped or replaced its
        // changes (commit or rollback on a locked connection, a new structure) – the rows read belong to none of them then.
        if (Refusal(rows.Count, fill) is { } late)
        {
            return late;
        }

        return ReferenceEquals(grid.Changes, changes)
            ? Fill([.. rows.Select((row, i) => (from + i, row))], marks.ColumnIndexes, fill)
            : GridFill.Refused(grid.RowNotLoaded);
    }

    /// <summary>A value for one column of the selected rows (WP-59: Ctrl+Enter in a cell editor, "Set value…" of the cell menu).</summary>
    public GridFill FillRows(IReadOnlyList<int> rowIndexes, int column, CellFill fill)
    {
        if (Refusal(rowIndexes.Count, fill) is { } refused)
        {
            return refused;
        }

        var rows = Loaded(rowIndexes);
        return rows.Count < rowIndexes.Count ? GridFill.Refused(grid.RowNotLoaded) : Fill(rows, [column], fill);
    }

    /// <summary>Why a command for so many rows cannot run now; null if it can.</summary>
    private GridFill? Refusal(int rows, CellFill fill)
    {
        if (grid.Changes is null)
        {
            return GridFill.Refused(GridText.Grid_TableNotEditable);
        }

        if (grid.WriteRunning)
        {
            return GridFill.Refused(GridText.Grid_WriteRunning);
        }

        // Before reading rows that are not loaded: the tracker would refuse them anyway.
        return ChangeTracker.FillRefusal(rows, fill) is { } refusal ? GridFill.Refused(refusal) : null;
    }

    private GridFill Fill(IReadOnlyList<(int Index, RowData Row)> rows, IReadOnlyList<int> columns, CellFill fill)
    {
        var changes = grid.Changes!;
        var result = changes.SetCells([.. rows.Select(r => r.Row)], columns, fill);
        if (result.Error is not null)
        {
            return GridFill.Refused(result.Error);
        }

        grid.ChangesMade();
        var changed = result.Changed.ToHashSet();
        var updates = new List<GridRowUpdate>();
        foreach (var (index, row) in rows.Where(r => changed.Contains(r.Row.Key)))
        {
            grid.QueueDisplayNames(changes.Find(row.Key) is { } change ? ValuesOf(change) : row.Values);

            // Sorted or reloaded while rows were read: another row may stand at the index now – only the row itself is drawn.
            if (grid.RowAt(index) is { } shown && shown.Key.Equals(row.Key))
            {
                updates.Add(new GridRowUpdate(index, Draw(changes, row)));
            }
        }

        grid.Notify(FillNotice.Text(result));
        return new GridFill([.. updates], null);
    }

    /// <summary>The loaded rows of grid indexes, in order; indexes whose block is gone are left out.</summary>
    private List<(int Index, RowData Row)> Loaded(IEnumerable<int> rowIndexes) =>
        [.. rowIndexes.Select(i => (Index: i, Row: grid.RowAt(i))).Where(r => r.Row is not null).Select(r => (r.Index, r.Row!))];

    private Dictionary<string, object?> Draw(ChangeTracker changes, RowData row) => grid.Draw(changes.Find(row.Key), row.Values);

    /// <summary>The values of a row with its pending ones (the display names look them up).</summary>
    private IReadOnlyList<object?> ValuesOf(RowChange change) => Enumerable.Range(0, details.Columns.Count).Select(change.ValueOf).ToList();
}
