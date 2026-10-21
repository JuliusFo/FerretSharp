using FerretSharp.UI.State;

namespace FerretSharp.UI.Tests.State;

/// <summary>The number of selected rows in the footers of table tabs and query results.</summary>
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
}
