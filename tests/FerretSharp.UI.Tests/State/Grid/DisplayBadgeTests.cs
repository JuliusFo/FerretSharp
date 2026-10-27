using FerretSharp.Core.Display;
using FerretSharp.Core.Schema;
using FerretSharp.UI.State;

namespace FerretSharp.UI.Tests.State;

/// <summary>WP-65: the badge of a display column in the grid header and the columns view – numbered when there are several.</summary>
public sealed class DisplayBadgeTests
{
    private static ColumnInfo Col(string name) => new(name, "VARCHAR2", 50, false, null, null, true, false, null, 1);

    private static readonly TableDetails Mitarbeiter = new(new TableSummary("APP", "MITARBEITER", TableKind.Table),
        [Col("ID"), Col("VORNAME"), Col("NACHNAME")], ["ID"], [], false);

    [Fact]
    public void The_only_display_column_is_name_and_several_are_numbered_in_their_order()
    {
        var one = DisplayColumns.Empty.With("MITARBEITER", ["NACHNAME"]);
        var two = DisplayColumns.Empty.With("MITARBEITER", ["NACHNAME", "GIBT_ES_NICHT", "VORNAME"]);

        Assert.Equal([null, null, "Name"], Enumerable.Range(0, 3).Select(i => GridColumn.DisplayBadgeOf(Mitarbeiter, one, i)));
        Assert.Equal([null, "Name 2", "Name 1"], Enumerable.Range(0, 3).Select(i => GridColumn.DisplayBadgeOf(Mitarbeiter, two, i)));
    }

    [Fact]
    public void A_view_has_no_display_column()
    {
        var view = Mitarbeiter with { Table = Mitarbeiter.Table with { Kind = TableKind.View } };

        Assert.Null(GridColumn.DisplayBadgeOf(view, DisplayColumns.Empty.With("MITARBEITER", ["NACHNAME"]), 2));
    }
}
