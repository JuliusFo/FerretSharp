using FerretSharp.Core.Workspaces;
using FerretSharp.UI.State;

namespace FerretSharp.UI.Tests.State;

/// <summary>R6b (review 3.40.0, B11): one list of a tab's views for the view switch and the link dialog.</summary>
public class TabModeLabelsTests
{
    [Fact]
    public void Every_view_has_a_label_once_in_the_order_of_the_switch()
    {
        Assert.Equal(Enum.GetValues<TabMode>(), TabModeLabels.All.Select(m => m.Mode));
        Assert.Equal("Spalten", TabModeLabels.Of(TabMode.Structure));
        using (UiCulture.Use("en"))
        {
            Assert.Equal("Columns", TabModeLabels.Of(TabMode.Structure));
        }
    }
}
