using FerretSharp.Core.Query;
using FerretSharp.Core.Settings;
using FerretSharp.Core.Workspaces;
using FerretSharp.UI.State;

namespace FerretSharp.UI.Tests.State;

/// <summary>How a newly opened table tab starts: the view of the setting, its rows now or on request.</summary>
public class TableOpeningTests
{
    [Fact]
    public void A_view_this_version_does_not_know_opens_the_data()
    {
        Assert.Equal(TabMode.Relations, TableOpening.View(new AppSettings { OpenView = TabMode.Relations }));
        Assert.Equal(TabMode.Data, TableOpening.View(new AppSettings { OpenView = (TabMode)99 })); // a number written by hand
    }

    [Theory]
    [InlineData(TabMode.Usage, false, TabMode.Data)]
    [InlineData(TabMode.Usage, true, TabMode.Usage)]
    [InlineData(TabMode.Ddl, false, TabMode.Ddl)]
    public void Usage_without_a_linked_project_shows_the_data(TabMode view, bool codeLinked, TabMode shown) =>
        Assert.Equal(shown, TableOpening.Shown(view, codeLinked));

    /// <summary>
    /// The grid's first request and a reload in the tab's order keep waiting; a click on a header comes with another order and
    /// loads – from then on every request does. So does a later block (code review: a counted total gives the grid a scroll
    /// range only until its first block answers again with no rows; a request past it would be one of the user's).
    /// </summary>
    [Theory]
    [InlineData(0, true)]
    [InlineData(500, false)]
    public void Deferred_rows_wait_until_the_grid_asks_in_another_order_or_for_a_later_block(int startRow, bool descending)
    {
        var byId = new SortSpec("ID", false);
        var tab = new TableTab(Guid.NewGuid(), TestApp.Kunden) { RowsDeferred = true, Sorts = [byId] };

        Assert.True(tab.StillDeferred(0, [byId]));
        Assert.True(tab.RowsDeferred);

        Assert.False(tab.StillDeferred(startRow, [byId with { Descending = descending }]));
        Assert.False(tab.RowsDeferred);
        Assert.False(tab.StillDeferred(0, [byId]));
    }
}
