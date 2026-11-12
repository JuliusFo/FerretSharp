using FerretSharp.Core.Schema;
using FerretSharp.Core.Workspaces;

namespace FerretSharp.Core.Tests.Workspaces;

public class ColumnPinningTests
{
    private static ColumnInfo Col(string name) => new(name, "VARCHAR2", 50, true, null, null, true, false, null, 0);

    // Composite PK in non-leading schema positions: PK order in the grid follows the schema.
    private static readonly TableDetails Position = new(
        new TableSummary("APP", "POSITION", TableKind.Table),
        [Col("NOTIZ"), Col("AUFTRAG_NR"), Col("ARTIKEL"), Col("POS_NR"), Col("MENGE"), Col("PREIS")],
        ["POS_NR", "AUFTRAG_NR"], [], false);

    private static readonly TableDetails NoKey = new(
        new TableSummary("APP", "V_UMSATZ", TableKind.View), [Col("A"), Col("B"), Col("C")], [], [], false);

    /// <summary>
    /// The primary key first, then the user's pins in pin order, then the schema order; a pin of a column that is gone does
    /// not count (review 3.70.0, C17: four tests of one call).
    /// </summary>
    [Theory]
    [InlineData(true, "", new[] { 1, 3, 0, 2, 4, 5 })]
    [InlineData(true, "PREIS,ARTIKEL", new[] { 1, 3, 5, 2, 0, 4 })]
    [InlineData(false, "C", new[] { 2, 0, 1 })] // without a primary key the pins come first
    [InlineData(false, "GELOESCHT", new[] { 0, 1, 2 })]
    public void Display_order_is_the_primary_key_then_the_pins_then_the_schema_order(bool withKey, string pins, int[] expected) =>
        Assert.Equal(expected, ColumnPinning.DisplayOrder(withKey ? Position : NoKey, pins.Split(',', StringSplitOptions.RemoveEmptyEntries)));

    [Fact]
    public void Primary_key_is_always_pinned_and_cannot_be_released()
    {
        Assert.True(ColumnPinning.IsPinned(Position, [], "POS_NR"));
        Assert.Empty(ColumnPinning.Unpin(Position, [], "POS_NR"));
        Assert.Empty(ColumnPinning.Toggle(Position, [], "AUFTRAG_NR"));
    }

    [Fact]
    public void Pin_appends_and_toggle_releases()
    {
        var pinned = ColumnPinning.Pin(Position, ["MENGE"], "ARTIKEL");
        Assert.Equal(["MENGE", "ARTIKEL"], pinned);
        Assert.True(ColumnPinning.IsPinned(Position, pinned, "ARTIKEL"));

        Assert.Equal(["ARTIKEL"], ColumnPinning.Toggle(Position, pinned, "MENGE"));
        Assert.Equal(["MENGE", "ARTIKEL", "NOTIZ"], ColumnPinning.Toggle(Position, pinned, "NOTIZ"));
    }

    [Fact]
    public void Pinning_twice_keeps_the_original_position() =>
        Assert.Equal(["MENGE", "ARTIKEL"], ColumnPinning.Pin(Position, ["MENGE", "ARTIKEL"], "MENGE"));

    [Fact]
    public void Normalize_drops_dropped_columns_primary_key_and_duplicates_and_respects_case() =>
        Assert.Equal(["PREIS", "MENGE"], ColumnPinning.Normalize(Position, ["GELOESCHT", "PREIS", "POS_NR", "preis", "PREIS", "MENGE"]));
}
