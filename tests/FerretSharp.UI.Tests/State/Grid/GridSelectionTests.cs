using FerretSharp.UI.State;

namespace FerretSharp.UI.Tests.State;

/// <summary>The number of selected rows, or the marked cells or columns (WP-58), in the footers of table tabs and query results.</summary>
public sealed class GridSelectionTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)] // a click always selects one row
    public void No_row_or_one_says_nothing(int count)
    {
        Assert.Null(new GridSelection(count, ToEnd: true).FooterText(allLoaded: false));
        Assert.Null(new GridSelection(count, ToEnd: true).FooterTitle(allLoaded: false));
    }

    [Fact]
    public void Several_rows_are_counted_in_both_languages()
    {
        // Below 1000: the group separator follows the format culture of the machine, not the UI language.
        var selection = new GridSelection(12, ToEnd: false);

        Assert.Equal("12 markiert", selection.FooterText(allLoaded: false));
        Assert.Null(selection.FooterTitle(allLoaded: false));
        using (UiCulture.Use("en"))
        {
            Assert.Equal("12 selected", selection.FooterText(allLoaded: false));
        }
    }

    [Fact]
    public void A_selection_to_the_end_says_only_loaded_rows_count_while_rows_are_missing()
    {
        var selection = new GridSelection(500, ToEnd: true);

        Assert.Equal("500 markiert (geladene Zeilen)", selection.FooterText(allLoaded: false));
        Assert.StartsWith("Nur geladene Zeilen sind markiert", selection.FooterTitle(allLoaded: false));
        Assert.Equal("500 markiert", selection.FooterText(allLoaded: true));
        Assert.Null(selection.FooterTitle(allLoaded: true));
        using (UiCulture.Use("en"))
        {
            Assert.Equal("500 selected (loaded rows)", selection.FooterText(allLoaded: false));
        }
    }

    [Fact]
    public void Marked_cells_are_counted_as_rows_by_columns_instead_of_selected_rows()
    {
        var selection = new GridSelection(0, ToEnd: false, MarkedRows: 3, MarkedColumns: 4);

        Assert.Equal("3 × 4 Zellen markiert", selection.FooterText(allLoaded: false));
        Assert.Equal("1 Zelle markiert", new GridSelection(0, false, 1, 1).FooterText(allLoaded: false));
        Assert.Null(selection.FooterTitle(allLoaded: false));
        using (UiCulture.Use("en"))
        {
            Assert.Equal("3 × 4 cells selected", selection.FooterText(allLoaded: false));
        }
    }

    [Fact]
    public void Marked_columns_mean_all_rows()
    {
        Assert.Equal("2 Spalten markiert · alle Zeilen", new GridSelection(0, false, 0, 2, WholeColumns: true).FooterText(allLoaded: false));
        Assert.Equal("1 Spalte markiert · alle Zeilen", new GridSelection(0, false, 0, 1, WholeColumns: true).FooterText(allLoaded: true));
    }

    [Fact]
    public void Marks_name_their_cells_or_columns_and_give_the_column_indexes_in_display_order()
    {
        var cells = new GridMarks(4, 6, ["c3", "c0"], Whole: false);
        var columns = new GridMarks(-1, -1, ["c2", "c5"], Whole: true);

        Assert.Equal(3, cells.Rows);
        Assert.Equal([3, 0], cells.ColumnIndexes);
        Assert.Equal("3 × 2 Zellen", cells.Text);
        Assert.Equal("1 Zelle", new GridMarks(4, 4, ["c1"], false).Text);
        Assert.Equal(0, columns.Rows);
        Assert.Equal("2 Spalten · alle Zeilen", columns.Text);
        using (UiCulture.Use("en"))
        {
            Assert.Equal("1 column · all rows", (columns with { Columns = ["c2"] }).Text);
        }
    }
}
