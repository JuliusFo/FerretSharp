using FerretSharp.Core.Data;
using FerretSharp.Core.Schema;

namespace FerretSharp.Core.Tests.Data;

/// <summary>WP-59: one value, NULL or the top row's values for many cells at once, as one step of the edit history.</summary>
public sealed class CellFillTests
{
    private static ColumnInfo Col(string name, string type, int? length = null, int? precision = null, int? scale = null,
        bool nullable = true, bool identity = false, bool isVirtual = false) =>
        new(name, type, length, true, precision, scale, nullable, identity, null, 0, IsVirtual: isVirtual);

    private static readonly TableDetails Kunden = new(
        new TableSummary("APP", "KUNDEN", TableKind.Table),
        [
            Col("ID", "NUMBER", precision: 10, scale: 0, nullable: false),
            Col("NAME", "VARCHAR2", 10, nullable: false),
            Col("KZ", "VARCHAR2", 3),
            Col("BETRAG", "NUMBER", precision: 6, scale: 2),
            Col("NOTIZ", "CLOB"),
            Col("LFD", "NUMBER", identity: true),
            Col("GROSS", "VARCHAR2", 10, isVirtual: true),
        ],
        ["ID"], [], false);

    private static int I(string name) => Kunden.Columns.ToList().FindIndex(c => c.Name == name);

    private static RowData Row(decimal id, string? name = "Meier", object? betrag = null) =>
        new(new RowKey.PrimaryKey([id]), [id, name, null, betrag, null, 1m, name?.ToUpperInvariant()]);

    private static readonly RowData[] Rows = [Row(1, betrag: 1m), Row(2, betrag: 2m), Row(3, betrag: 3m)];

    private static object? Value(ChangeTracker changes, RowData row, string column) =>
        changes.Find(row.Key) is { } change ? change.ValueOf(I(column)) : row.Values[I(column)];

    [Fact]
    public void A_value_goes_into_every_cell_as_one_step_of_the_history()
    {
        var changes = new ChangeTracker(Kunden);

        var result = changes.SetCells(Rows, [I("NAME"), I("KZ")], CellFill.Value("ABC"));

        Assert.Null(result.Error);
        Assert.Equal(6, result.Cells);
        Assert.All(Rows, r => Assert.Equal(["ABC", "ABC"], [Value(changes, r, "NAME"), Value(changes, r, "KZ")]));

        Assert.Equal(3, changes.UndoEdit().Count);
        Assert.All(Rows, r => Assert.Null(changes.Find(r.Key)));
        Assert.False(changes.CanUndoEdit);

        changes.RedoEdit();
        Assert.All(Rows, r => Assert.Equal("ABC", Value(changes, r, "KZ")));
    }

    [Fact]
    public void A_value_one_column_cannot_take_sets_nothing_and_names_the_column()
    {
        var changes = new ChangeTracker(Kunden);

        var several = changes.SetCells(Rows, [I("NAME"), I("BETRAG")], CellFill.Value("zwölf"));
        var one = changes.SetCells(Rows, [I("BETRAG")], CellFill.Value("zwölf"));

        Assert.StartsWith("BETRAG: ", several.Error);
        Assert.Contains("keine Zahl", several.Error);
        Assert.DoesNotContain("BETRAG", one.Error); // the message stands below the column's editor
        Assert.Equal(0, several.Cells);
        Assert.False(changes.HasChanges);
        Assert.False(changes.CanUndoEdit);
    }

    [Fact]
    public void Columns_that_cannot_take_a_value_are_left_as_they_are_with_the_reason()
    {
        var changes = new ChangeTracker(Kunden);

        var result = changes.SetCells(Rows, [I("ID"), I("NAME"), I("NOTIZ"), I("LFD"), I("GROSS")], CellFill.Value("X"));

        Assert.Null(result.Error);
        Assert.Equal(3, result.Cells);
        Assert.Equal(["ID", "NOTIZ", "LFD", "GROSS"], result.Columns.Select(c => c.Name));
        Assert.Contains("Primärschlüssel", result.Columns[0].Reason);
        Assert.Contains("LOB-Editor", result.Columns[1].Reason);
        Assert.All(Rows, r => Assert.Equal(r.Values[I("ID")], Value(changes, r, "ID")));
    }

    [Fact]
    public void Null_leaves_not_null_columns_as_they_are()
    {
        var changes = new ChangeTracker(Kunden);
        changes.SetValue(Rows[0], I("KZ"), "ABC");

        var result = changes.SetCells(Rows, [I("NAME"), I("KZ"), I("BETRAG")], CellFill.Null);

        Assert.Equal(6, result.Cells);
        Assert.Equal("NAME", Assert.Single(result.Columns).Name);
        Assert.Contains("darf nicht leer sein", result.Columns[0].Reason);
        Assert.All(Rows, r => Assert.Equal([null, null], [Value(changes, r, "KZ"), Value(changes, r, "BETRAG")]));
        Assert.All(Rows, r => Assert.Equal("Meier", Value(changes, r, "NAME")));

        changes.UndoEdit();
        Assert.Equal("ABC", Value(changes, Rows[0], "KZ")); // back to the edit before, not to the database value
        Assert.Null(changes.Find(Rows[1].Key));
    }

    [Fact]
    public void Down_takes_the_top_row_as_shown_into_the_rows_below()
    {
        var changes = new ChangeTracker(Kunden);
        changes.SetValue(Rows[0], I("BETRAG"), "9");

        var result = changes.SetCells(Rows, [I("BETRAG"), I("KZ")], CellFill.Down);

        Assert.Equal(4, result.Cells);
        Assert.All(Rows, r => Assert.Equal(9m, Value(changes, r, "BETRAG")));
        Assert.Null(Value(changes, Rows[2], "KZ"));

        changes.UndoEdit();
        Assert.Equal(9m, Value(changes, Rows[0], "BETRAG"));
        Assert.Equal([2m, 3m], Rows[1..].Select(r => Value(changes, r, "BETRAG")));
    }

    [Fact]
    public void Down_leaves_a_column_whose_top_value_cannot_go_down()
    {
        var changes = new ChangeTracker(Kunden);
        RowData[] rows = [Row(1, name: null, betrag: new BigNumber("1.23456789012345678901234567890123")), Row(2)];

        var result = changes.SetCells(rows, [I("NAME"), I("BETRAG")], CellFill.Down);

        Assert.Equal(0, result.Cells);
        Assert.Equal(["NAME", "BETRAG"], result.Columns.Select(c => c.Name));
        Assert.Contains("darf nicht leer sein", result.Columns[0].Reason);
        Assert.Contains("28 Stellen", result.Columns[1].Reason);
        Assert.False(changes.CanUndoEdit);
    }

    [Fact]
    public void Rows_marked_for_deletion_and_numbers_too_precise_are_left_out()
    {
        var changes = new ChangeTracker(Kunden);
        RowData[] rows = [Row(1), Row(2), Row(3, betrag: new BigNumber("1.23456789012345678901234567890123"))];
        changes.Delete(rows[1]);

        var result = changes.SetCells(rows, [I("BETRAG")], CellFill.Value("5"));

        Assert.Equal((1, 1, 1), (result.Cells, result.DeletedRows, result.TooPrecise));
        Assert.Equal(5m, Value(changes, rows[0], "BETRAG"));
        Assert.Equal(ChangeStage.Pending, changes.Find(rows[1].Key)!.Deleted);
        Assert.Null(changes.Find(rows[2].Key));
    }

    [Fact]
    public void The_value_the_database_holds_ends_a_pending_change()
    {
        var changes = new ChangeTracker(Kunden);
        changes.SetValue(Rows[0], I("NAME"), "Schulz");

        changes.SetCells([Rows[0]], [I("NAME")], CellFill.Value("Meier"));

        Assert.Null(changes.Find(Rows[0].Key));
    }

    [Fact]
    public void Too_many_rows_and_a_fill_down_of_one_row_set_nothing()
    {
        var changes = new ChangeTracker(Kunden);
        var many = Enumerable.Range(1, ChangeTracker.MaxFillRows + 1).Select(i => Row(i)).ToList();

        Assert.Contains("10.000", changes.SetCells(many, [I("KZ")], CellFill.Null).Error);
        Assert.NotNull(changes.SetCells([Rows[0]], [I("KZ")], CellFill.Down).Error);
        Assert.False(changes.HasChanges);
    }

    [Fact]
    public void Deleting_and_reverting_several_rows_is_one_step_each()
    {
        var changes = new ChangeTracker(Kunden);
        changes.SetValue(Rows[0], I("KZ"), "ABC");

        changes.Delete(Rows);
        Assert.All(Rows, r => Assert.Equal(ChangeStage.Pending, changes.Find(r.Key)!.Deleted));
        changes.UndoEdit();
        Assert.Equal("ABC", Value(changes, Rows[0], "KZ"));
        Assert.Null(changes.Find(Rows[1].Key));

        changes.RedoEdit();
        changes.Revert([.. Rows.Select(r => r.Key)]);
        Assert.False(changes.HasChanges);
        changes.UndoEdit();
        Assert.All(Rows, r => Assert.Equal(ChangeStage.Pending, changes.Find(r.Key)!.Deleted));
    }
}
