using FerretSharp.UI.Resources;
using FerretSharp.UI.State;

namespace FerretSharp.UI.Tests.State;

/// <summary>The pin action of both menus (WP-61; review 3.54.0, C6: the rule stood twice in the markup without a test).</summary>
public sealed class PinActionTests
{
    [Fact]
    public void A_key_column_is_pinned_by_its_key_others_by_the_user()
    {
        Assert.Equal(new PinAction(GridText.ColumnMenu_PkPinned, true, GridText.ColumnMenu_PkPinnedTitle), PinAction.Of(pinned: false, primaryKey: true));
        Assert.Equal(new PinAction(GridText.Grid_UnpinColumn, true, null), PinAction.Of(pinned: true, primaryKey: false));
        Assert.Equal(new PinAction(GridText.Grid_PinColumnLeft, false, null), PinAction.Of(pinned: false, primaryKey: false));
    }
}
