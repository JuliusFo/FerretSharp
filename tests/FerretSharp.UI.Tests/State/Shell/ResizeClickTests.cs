using FerretSharp.UI.State;

namespace FerretSharp.UI.Tests.State;

/// <summary>A double click on a drag handle resets the size; the click that ends a drag never does.</summary>
public sealed class ResizeClickTests
{
    // Positions as in the E2E of the review (2026-11-04): pressed at 228, released after dragging at 450. "along" is the
    // axis the handle drags on, "across" the other one; each case runs for a bottom (vertical) and a side handle.
    [Theory]
    [InlineData(2, 0, 228, 0, true)] // double click where pressed
    [InlineData(3, 0, 229, 0, true)] // the host's duplicated mouse-down counts to 3; a pixel of jitter
    [InlineData(2, 0, 228, 10, true)] // slipped across the drag axis: dom.js did not drag, so it resets
    [InlineData(1, 0, 228, 0, false)] // single click
    [InlineData(2, 2, 228, 0, false)] // right button
    [InlineData(3, 0, 450, 0, false)] // click, then press and drag at once: the click after the drag
    [InlineData(2, 0, 231, 0, false)] // released at the drag threshold
    public void Only_a_double_click_where_the_button_went_down_resets(long detail, long button, double along, double across, bool reset)
    {
        Assert.Equal(reset, ResizeClick.IsReset(detail, button, vertical: true, (550, 228), 550 + across, along));
        Assert.Equal(reset, ResizeClick.IsReset(detail, button, vertical: false, (228, 550), along, 550 + across));
    }

    [Fact]
    public void A_double_click_without_a_press_seen_still_resets() =>
        Assert.True(ResizeClick.IsReset(2, 0, vertical: true, null, 550, 228));
}
