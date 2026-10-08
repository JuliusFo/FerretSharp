using FerretSharp.Core.ClrModel;
using FerretSharp.Core.Data;
using FerretSharp.Core.Schema;
using FerretSharp.UI.State;

namespace FerretSharp.UI.Tests.State;

/// <summary>The row format grid.js reads (column ids, display text, markers) – a contract that had no test before R3b.</summary>
public sealed class GridRowsTests
{
    private static readonly TableDetails Kunden = new(
        new TableSummary("APP", "KUNDEN", TableKind.Table),
        [
            new ColumnInfo("ID", "NUMBER", null, false, 10, 0, false, false, null, 1),
            new ColumnInfo("NAME", "VARCHAR2", 50, true, null, null, true, false, null, 2),
            new ColumnInfo("GROSS", "NUMBER", null, false, 38, 0, true, false, null, 3),
        ],
        ["ID"], [], false);

    private static readonly TablePresentation Plain = TablePresentation.Plain(Kunden);

    private static RowData Row(decimal id, string? name, object? gross = null) =>
        new(new RowKey.PrimaryKey([id]), [id, name, gross]);

    [Fact]
    public void Loaded_row_has_display_text_by_column_id_and_null_for_NULL()
    {
        var cells = GridRows.Build(Plain, null, Row(4711m, null).Values);

        Assert.Equal("4.711", cells["c0"]);
        Assert.Null(cells["c1"]);
        Assert.False(cells.ContainsKey("__s"));
        Assert.False(cells.ContainsKey("__new"));
    }

    [Fact]
    public void Numbers_beyond_decimal_cannot_be_edited_in_the_cell()
    {
        var cells = GridRows.Build(Plain, null, Row(1m, "x", new BigNumber("123456789012345678901234567890123")).Values);

        Assert.Equal(["c2"], Assert.IsType<List<string>>(cells["__ro"]));
    }

    [Fact]
    public void Changed_and_deleted_rows_carry_their_stage()
    {
        var changes = new ChangeTracker(Kunden);
        var row = Row(1m, "Meier");
        Assert.True(changes.SetValue(row, 1, "Müller").IsValid);

        var changed = GridRows.Build(Plain, changes.Find(row.Key), row.Values);

        Assert.Equal("Müller", changed["c1"]);
        Assert.Equal(new Dictionary<string, string> { ["c1"] = "p" }, changed["__s"]);

        changes.Delete(row);
        Assert.Equal("p", GridRows.Build(Plain, changes.Find(row.Key), row.Values)["__d"]);
    }

    [Fact]
    public void New_rows_are_found_by_their_id()
    {
        var changes = new ChangeTracker(Kunden);
        changes.AddRow();
        var second = changes.AddRow();

        var cells = GridRows.Build(Plain, second, null);
        var id = Assert.IsType<string>(cells["__new"]);

        Assert.Same(second, GridRows.NewRow(changes, id));
        Assert.Equal(1, GridRows.NewRowIndex(changes, id));
        Assert.Null(GridRows.NewRow(changes, "unknown"));
        Assert.Equal(-1, GridRows.NewRowIndex(null, id));
    }
}
