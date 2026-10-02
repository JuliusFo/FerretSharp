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

    [Fact]
    public void Without_user_pins_the_primary_key_comes_first_then_the_schema_order() =>
        Assert.Equal([1, 3, 0, 2, 4, 5], ColumnPinning.DisplayOrder(Position, []));

    [Fact]
    public void User_pins_follow_the_primary_key_in_pin_order() =>
        Assert.Equal([1, 3, 5, 2, 0, 4], ColumnPinning.DisplayOrder(Position, ["PREIS", "ARTIKEL"]));

    [Fact]
    public void Table_without_primary_key_starts_with_the_user_pins() =>
        Assert.Equal([2, 0, 1], ColumnPinning.DisplayOrder(NoKey, ["C"]));

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

    [Fact]
    public void Display_order_ignores_columns_that_no_longer_exist() =>
        Assert.Equal([0, 1, 2], ColumnPinning.DisplayOrder(NoKey, ["GELOESCHT"]));
}
