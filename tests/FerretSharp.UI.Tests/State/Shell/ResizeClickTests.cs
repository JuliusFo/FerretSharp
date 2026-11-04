using FerretSharp.UI.State;

namespace FerretSharp.UI.Tests.State;

/// <summary>A double click on a drag handle resets the size; the click that ends a drag never does.</summary>
public sealed class ResizeClickTests
{
    // Positions as in the E2E of the review (2026-11-04): pressed at y 228, released after dragging at y 450.
    [Theory]
    [InlineData(2, 0, 228, 228, true)] // double click where pressed
    [InlineData(3, 0, 228, 229, true)] // the host's duplicated mouse-down counts to 3; a pixel of jitter
    [InlineData(1, 0, 228, 228, false)] // single click
    [InlineData(2, 2, 228, 228, false)] // right button
    [InlineData(3, 0, 228, 450, false)] // click, then press and drag at once: the click after the drag
    [InlineData(2, 0, 228, 231, false)] // released at the drag threshold
    public void Only_a_double_click_where_the_button_went_down_resets(long detail, long button, double pressY, double clickY, bool reset) =>
        Assert.Equal(reset, ResizeClick.IsReset(detail, button, (550, pressY), 550, clickY));

    [Fact]
    public void A_double_click_without_a_press_seen_still_resets() =>
        Assert.True(ResizeClick.IsReset(2, 0, null, 550, 228));
}
