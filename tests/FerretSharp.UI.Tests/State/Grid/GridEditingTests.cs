using FerretSharp.Core.Data;
using FerretSharp.Core.Schema;
using FerretSharp.UI.State;

namespace FerretSharp.UI.Tests.State;

/// <summary>R6b (review 3.40.0, B10): editing in the table grid, out of the component and testable.</summary>
public sealed class GridEditingTests
{
    private static ColumnInfo Col(string name, string type, bool nullable = true, bool identity = false, int? precision = null, int? scale = null) =>
        new(name, type, 30, true, precision, scale, nullable, identity, null, 0);

    private static readonly TableDetails Kunden = new(
        new TableSummary("APP", "KUNDEN", TableKind.Table),
        [Col("LFD", "NUMBER", nullable: false, identity: true), Col("ID", "NUMBER", nullable: false, precision: 10, scale: 0), Col("NAME", "VARCHAR2"), Col("NOTIZ", "CLOB")],
        ["ID"], [], false);

    private static RowData Row(decimal id, string name) => new(new RowKey.PrimaryKey([id]), [id, id, name, null]);

    private readonly FakeGrid _grid = new();

    private GridEditing Editing => new(_grid, Kunden);

    [Fact]
    public void A_typed_value_is_pending_drawn_and_its_names_looked_up()
    {
        _grid.Rows[0] = Row(1, "Peter");

        var edit = Editing.Edit(0, null, 2, "Petra");

        Assert.Null(edit.Error);
        Assert.NotNull(edit.Row);
        Assert.Equal("Petra", _grid.Changes!.Find(new RowKey.PrimaryKey([1m]))!.ValueOf(2));
        Assert.Equal([1m, 1m, "Petra", null], Assert.Single(_grid.Queued));
        Assert.Equal(1, _grid.ChangesMadeCount);
    }

    [Fact]
    public void An_invalid_value_says_why_and_draws_nothing()
    {
        _grid.Rows[0] = Row(1, "Peter");

        var edit = Editing.Edit(0, null, 1, "eins");

        Assert.NotNull(edit.Error);
        Assert.Null(edit.Row);
        Assert.Empty(_grid.Queued);
    }

    [Fact]
    public void Nothing_is_kept_without_changes_while_writing_or_for_a_row_no_longer_loaded()
    {
        Assert.Equal("Zeile nicht geladen", Editing.Edit(5, null, 2, "x").Error);

        _grid.WriteRunning = true;
        Assert.Equal("Änderungen werden gerade geschrieben – bearbeite weiter, sobald das fertig ist.", Editing.Edit(0, null, 2, "x").Error);

        _grid.Changes = null;
        Assert.Equal("Diese Tabelle lässt sich hier nicht bearbeiten.", Editing.Edit(0, null, 2, "x").Error);
        Assert.Equal(0, _grid.ChangesMadeCount);
    }

    [Fact]
    public async Task A_new_row_starts_at_the_first_column_that_can_be_typed_into_and_takes_values()
    {
        var added = await Editing.AddRowAsync([0, 1, 2, 3]);

        Assert.NotNull(added);
        Assert.Equal(1, added.Column); // LFD is an identity column
        Assert.Equal(0, added.PinnedIndex);
        Assert.Equal(1, _grid.NewRowsSet);

        var edit = Editing.Edit(-1, GridRows.NewRowId(added.Row), 2, "Neu");
        Assert.Null(edit.Error);
        Assert.Equal("Neu", added.Row.ValueOf(2));
    }

    [Fact]
    public async Task Del_marks_the_selected_loaded_rows()
    {
        _grid.Rows[0] = Row(1, "Peter");
        _grid.Rows[1] = Row(2, "Paul");
        var added = _grid.Changes!.AddRow();

        var updates = await Editing.DeleteKeyAsync([0, 1, 7], null);

        Assert.Equal([0, 1], updates.Select(u => u.RowIndex));
        Assert.Equal(ChangeStage.Pending, _grid.Changes.Find(new RowKey.PrimaryKey([2m]))!.Deleted);
        Assert.Same(added, Assert.Single(_grid.Changes.NewRows));
        Assert.Equal(0, _grid.NewRowsSet);
    }

    [Fact]
    public async Task Del_on_a_new_row_drops_only_it_and_spares_the_loaded_rows_still_selected()
    {
        // AG Grid does not select rows pinned at the top: the last clicked loaded row stays selected while a new row has the focus.
        _grid.Rows[0] = Row(1, "Peter");
        var first = _grid.Changes!.AddRow();
        var second = _grid.Changes.AddRow();

        var updates = await Editing.DeleteKeyAsync([0], GridRows.NewRowId(second));

        Assert.Empty(updates);
        Assert.Null(_grid.Changes.Find(new RowKey.PrimaryKey([1m])));
        Assert.Same(first, Assert.Single(_grid.Changes.NewRows));
        Assert.Equal(1, _grid.NewRowsSet);
    }

    [Fact]
    public async Task While_writing_delete_revert_and_new_rows_say_so_and_change_nothing()
    {
        _grid.Rows[0] = Row(1, "Peter");
        _grid.WriteRunning = true;

        Assert.Empty(await Editing.DeleteKeyAsync([0], null));
        await Editing.ChangeRowsAsync([0], delete: true);
        Assert.Null(await Editing.AddRowAsync([0, 1, 2]));

        Assert.Equal(0, _grid.Changes!.PendingCount);
        Assert.Equal(3, _grid.Notices.Count);
    }

    [Fact]
    public async Task The_cell_menu_reverts_a_row_and_redraws_it()
    {
        _grid.Rows[0] = Row(1, "Peter");
        Editing.Edit(0, null, 2, "Petra");

        await Editing.ChangeRowsAsync([0], delete: false);

        Assert.Equal(0, _grid.Changes!.PendingCount);
        Assert.Equal(0, Assert.Single(Assert.Single(_grid.Updated)).RowIndex);
    }

    [Fact]
    public async Task A_lob_value_of_a_loaded_row_is_pending_and_redrawn()
    {
        _grid.Rows[0] = Row(1, "Peter");

        var error = await Editing.SetContentAsync((0, _grid.Rows[0]), null, 3, "alt", "neu");

        Assert.Null(error);
        Assert.NotNull(_grid.Changes!.Find(new RowKey.PrimaryKey([1m])));
        Assert.Single(_grid.Updated);
    }

    private sealed class FakeGrid : IEditableGrid
    {
        public ChangeTracker? Changes { get; set; } = new(Kunden);

        public bool WriteRunning { get; set; }

        public Dictionary<int, RowData> Rows { get; } = [];

        public List<IReadOnlyList<object?>> Queued { get; } = [];

        public List<List<GridRowUpdate>> Updated { get; } = [];

        public List<string> Notices { get; } = [];

        public int ChangesMadeCount { get; private set; }

        public int NewRowsSet { get; private set; }

        public RowData? RowAt(int rowIndex) => Rows.GetValueOrDefault(rowIndex);

        public Dictionary<string, object?> Draw(RowChange? change, IReadOnlyList<object?>? values) => new() { ["change"] = change, ["values"] = values };

        public Task UpdateRowsAsync(List<GridRowUpdate> updates)
        {
            Updated.Add(updates);
            return Task.CompletedTask;
        }

        public Task SetNewRowsAsync()
        {
            NewRowsSet++;
            return Task.CompletedTask;
        }

        public void QueueDisplayNames(IReadOnlyList<object?> values) => Queued.Add(values);

        public void ChangesMade() => ChangesMadeCount++;

        public void Notify(string text) => Notices.Add(text);

        public string RowNotLoaded => "Zeile nicht geladen";
    }
}
