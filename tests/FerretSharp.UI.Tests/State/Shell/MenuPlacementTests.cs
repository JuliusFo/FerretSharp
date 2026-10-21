using FerretSharp.UI.State;

namespace FerretSharp.UI.Tests.State;

/// <summary>R6b (review 3.40.0, B4): where a menu button opens its menu – PopoverMenu then keeps it inside the window.</summary>
public class MenuPlacementTests
{
    [Fact]
    public void The_menu_opens_below_the_mouse_with_its_right_edge_near_it()
    {
        var at = MenuPlacement.Below(clientX: 500, clientY: 100, menuWidth: 220, shiftX: 40, shiftY: 14, viewportWidth: 1200, viewportHeight: 800);

        Assert.Equal(new MenuPlacement(320, 114, 1200, 800), at);
    }

    [Fact]
    public void Near_the_left_edge_it_keeps_a_margin()
    {
        Assert.Equal(8, MenuPlacement.Below(30, 100, 220, 0, 10, 1200, 800).X);
    }

    [Fact]
    public void A_menu_starting_at_the_mouse_shifts_by_its_width()
    {
        // The workspace bar: the menu starts 16 px left of the mouse.
        Assert.Equal(484, MenuPlacement.Below(500, 100, 240, 240 - 16, 12, 1200, 800).X);
    }
}
