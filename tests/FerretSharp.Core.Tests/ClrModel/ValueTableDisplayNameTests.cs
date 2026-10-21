using FerretSharp.Core.Query;
using FerretSharp.Core.ClrModel;
using FerretSharp.Core.Data;
using FerretSharp.Core.Resources;
using FerretSharp.Core.Schema;
using FerretSharp.Core.Settings;

namespace FerretSharp.Core.Tests.ClrModel;

public sealed class ValueTableDisplayNameTests
{
    private static readonly ColumnInfo Art = new("ART", "NUMBER", null, false, 2, 0, false, false, null, 1);

    private static readonly PropertyExport Auftragsart = new("Art", "Auftragsart", "Shop.Auftragsart", false, false, "ART", null, null, null, false,
        [new("ProductionOrder", "0", "0", "Fertigungsauftrag"), new("Service", "1", "1")]);

    [Fact]
    public void The_display_text_replaces_the_member_name_which_stays_in_the_tooltip()
    {
        var table = ValueTable.For(Auftragsart, Art)!;

        Assert.Equal(new PresentedValue("Fertigungsauftrag (0)", Tooltip: "Auftragsart.ProductionOrder"), table.Present(0m));
        Assert.Equal(new PresentedValue("Service (1)"), table.Present(1m)); // no [Display]: the member name, no tooltip
    }

    [Fact]
    public void Options_show_the_display_text_and_keep_the_member_and_the_stored_value()
    {
        var table = ValueTable.For(Auftragsart, Art)!;

        Assert.Equal(
            [new ValueOption("Fertigungsauftrag", "0", "Fertigungsauftrag (0)", "ProductionOrder"), new ValueOption("Service", "1", "Service (1)")],
            table.Options);
        Assert.Equal("Fertigungsauftrag", table.FindOption("0")?.Name);
        Assert.Equal("0", table.OptionOf(0m)?.Value);
        Assert.Null(table.OptionOf(5m));
    }
}
