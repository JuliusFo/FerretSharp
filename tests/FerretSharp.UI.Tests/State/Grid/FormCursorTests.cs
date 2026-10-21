using FerretSharp.Core.Data;
using FerretSharp.Core.Forms;
using FerretSharp.Core.Schema;
using FerretSharp.UI.State;

namespace FerretSharp.UI.Tests.State;

/// <summary>
/// The form follows the grid's row (WP-21): after writing and reloading the row may move, a new row becomes a loaded one,
/// rows may drop out of the loaded range. Before R3b this logic lived in the component and had no test.
/// </summary>
public sealed class FormCursorTests
{
    private static readonly TableDetails Kunden = new(
        new TableSummary("APP", "KUNDEN", TableKind.Table),
        [new ColumnInfo("ID", "NUMBER", null, false, 10, 0, false, false, null, 1), new ColumnInfo("NAME", "VARCHAR2", 50, true, null, null, true, false, null, 2)],
        ["ID"], [], false);

    private readonly FakeGrid _grid = new();

    private static RowData Row(decimal id) => new(new RowKey.PrimaryKey([id]), [id, $"Kunde {id}"]);

    [Fact]
    public void Shows_a_loaded_row_and_knows_its_key()
    {
        _grid.Load(Row(10), Row(11));
        var cursor = new FormCursor(_grid);

        cursor.Show(new GridCell(1, null, 0));

        Assert.Equal(Row(11).Key, cursor.Key);
        Assert.True(cursor.Shows(new GridCell(1, null, 1))); // another column of the same row
        Assert.False(cursor.Shows(new GridCell(0, null, 0)));
    }

    [Fact]
    public void Key_becomes_known_when_the_block_arrives()
    {
        var cursor = new FormCursor(_grid);
        cursor.Show(new GridCell(0, null, null)); // opened before the first block

        Assert.Null(cursor.Key);
        Assert.Equal(FormFollow.Unchanged, cursor.RowsChanged());

        _grid.Load(Row(10));

        Assert.Equal(FormFollow.KeyKnown, cursor.RowsChanged());
        Assert.Equal(Row(10).Key, cursor.Key);
    }

    [Fact]
    public void A_row_that_moved_after_reloading_is_found_by_its_key()
    {
        _grid.Load(Row(10), Row(11), Row(12));
        var cursor = new FormCursor(_grid);
        cursor.Show(new GridCell(1, null, 1));

        _grid.Load(Row(9), Row(10), Row(11), Row(12)); // a row was inserted before it

        Assert.Equal(FormFollow.Moved, cursor.RowsChanged());
        Assert.Equal(new GridCell(2, null, 1), cursor.Cell);
    }

    [Fact]
    public void A_row_no_longer_loaded_is_lost_with_a_reason()
    {
        _grid.Load(Row(10), Row(11));
        var cursor = new FormCursor(_grid);
        cursor.Show(new GridCell(1, null, null));

        _grid.Load(Row(20), Row(21));

        Assert.Equal(FormFollow.Lost, cursor.RowsChanged());
        Assert.Null(cursor.Cell);
        Assert.Contains("nicht mehr geladen", cursor.Lost);
    }

    [Fact]
    public void A_new_row_becomes_a_loaded_row_once_inserted()
    {
        _grid.Load(Row(10));
        var added = _grid.Changes.AddRow();
        var cursor = new FormCursor(_grid);
        cursor.Show(_grid.CellOf(added, 1)!);
        Assert.Same(added, cursor.NewRow);

        // Writing inserts it (key 99) and the grid reloads with it at position 1.
        var operations = _grid.Changes.PendingOperations();
        _grid.Changes.MarkFlushed(operations, new Dictionary<Guid, RowKey> { [added.Id] = Row(99).Key });
        _grid.Load(Row(10), Row(99));

        Assert.Equal(FormFollow.Moved, cursor.RowsChanged());
        Assert.Null(cursor.NewRow);
        Assert.Equal(new GridCell(1, null, 1), cursor.Cell);
    }

    [Fact]
    public void A_removed_new_row_is_lost()
    {
        var added = _grid.Changes.AddRow();
        var cursor = new FormCursor(_grid);
        cursor.Show(_grid.CellOf(added, null)!);

        _grid.Changes.Delete(added);

        Assert.Equal(FormFollow.Lost, cursor.RowsChanged());
        Assert.Contains("entfernt", cursor.Lost);
    }

    [Fact]
    public void Arrows_go_through_the_new_rows_first_then_the_loaded_rows()
    {
        _grid.Load(Row(10), Row(11));
        var first = _grid.Changes.AddRow();
        var second = _grid.Changes.AddRow();
        var newRows = _grid.Changes.NewRows;
        var cursor = new FormCursor(_grid);

        cursor.Show(_grid.CellOf(first, 0)!);
        Assert.Null(cursor.Target(-1, newRows, 1));
        Assert.Equal(_grid.CellOf(second, 0), cursor.Target(1, newRows, 1));
        Assert.Equal(new GridCell(0, null, 0), cursor.Target(2, newRows, 1));

        cursor.Show(new GridCell(0, null, 0));
        Assert.Equal(_grid.CellOf(second, 0), cursor.Target(-1, newRows, 1));
        Assert.Equal(new GridCell(1, null, 0), cursor.Target(1, newRows, 1));
        Assert.Null(cursor.Target(2, newRows, 1)); // past the last row
    }

    /// <summary>Loaded rows by position and the tab's changes, like FerretGrid's blocks.</summary>
    private sealed class FakeGrid : IFormGrid
    {
        private List<RowData> _rows = [];

        public ChangeTracker Changes { get; } = new(Kunden);

        public void Load(params RowData[] rows) => _rows = [.. rows];

        public FormRow? RowOf(GridCell cell) =>
            cell.IsNew ? GridRows.NewRow(Changes, cell.NewId) is { } added ? new FormRow(null, added) : null : RowAt(cell.RowIndex);

        public FormRow? RowAt(int rowIndex) =>
            rowIndex >= 0 && rowIndex < _rows.Count ? new FormRow(_rows[rowIndex], Changes.Find(_rows[rowIndex].Key)) : null;

        public int? IndexOf(RowKey key) => _rows.FindIndex(r => Equals(r.Key, key)) is >= 0 and var index ? index : null;

        public GridCell? CellOf(RowChange newRow, int? column) =>
            Changes.NewRows.Contains(newRow) ? new GridCell(-1, GridRows.NewRowId(newRow), column) : null;
    }
}
