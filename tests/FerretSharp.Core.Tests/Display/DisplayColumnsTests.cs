using FerretSharp.Core.Display;
using FerretSharp.Core.Schema;

namespace FerretSharp.Core.Tests.Display;

/// <summary>WP-65: the display columns of a table as an ordered list – changed one column at a time.</summary>
public sealed class DisplayColumnsTests
{
    private static ColumnInfo Col(string name, string type) => new(name, type, 50, false, null, null, true, false, null, 1);

    private static readonly TableDetails Mitarbeiter = new(new TableSummary("APP", "MITARBEITER", TableKind.Table),
        [Col("ID", "NUMBER"), Col("VORNAME", "VARCHAR2"), Col("NACHNAME", "VARCHAR2"), Col("FOTO", "BLOB")], ["ID"], [], false);

    [Fact]
    public void Adding_appends_once_and_removing_keeps_the_order_of_the_rest()
    {
        var columns = DisplayColumns.Empty.Adding("MITARBEITER", "NACHNAME").Adding("MITARBEITER", "VORNAME").Adding("MITARBEITER", "NACHNAME");

        Assert.Equal(["NACHNAME", "VORNAME"], columns.ColumnsOf("MITARBEITER"));
        Assert.Equal(["VORNAME"], columns.Removing("MITARBEITER", "NACHNAME").ColumnsOf("MITARBEITER"));
        Assert.Equal(0, columns.Removing("MITARBEITER", "NACHNAME").Removing("MITARBEITER", "VORNAME").Count); // the last one takes the entry
        Assert.Equal(["NACHNAME", "VORNAME"], columns.ColumnsOf("MITARBEITER")); // unchanged: every change is a copy
    }

    [Fact]
    public void With_leaves_out_blank_and_repeated_names()
    {
        var columns = DisplayColumns.Empty.With("MITARBEITER", ["NACHNAME", " ", "", "NACHNAME", "VORNAME"]);

        Assert.Equal(["NACHNAME", "VORNAME"], columns.ColumnsOf("MITARBEITER"));
        Assert.Empty(DisplayColumns.Empty.With("MITARBEITER", [" "]).ColumnsOf("MITARBEITER"));
    }

    [Fact]
    public void The_parts_of_a_name_are_the_columns_the_table_has_that_read_as_text()
    {
        var columns = DisplayColumns.Empty.With("MITARBEITER", ["FOTO", "NACHNAME", "GIBT_ES_NICHT", "VORNAME"]);

        Assert.Equal([3, 2, 1], columns.IndexesIn(Mitarbeiter));
        Assert.Equal([2, 1], columns.PartsIn(Mitarbeiter));
    }

    [Fact]
    public void The_same_columns_in_another_order_are_not_the_same()
    {
        var one = DisplayColumns.Empty.With("MITARBEITER", ["NACHNAME", "VORNAME"]);

        Assert.True(one.SameAs(DisplayColumns.Empty.With("MITARBEITER", ["NACHNAME", "VORNAME"])));
        Assert.False(one.SameAs(DisplayColumns.Empty.With("MITARBEITER", ["VORNAME", "NACHNAME"])));
    }
}
