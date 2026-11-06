using FerretSharp.Core.Settings;
using FerretSharp.UI.State;

namespace FerretSharp.UI.Tests.State;

/// <summary>Dragged sizes as CSS custom properties: only those set, the stylesheet keeps the rest.</summary>
public sealed class PanelSizesTests
{
    [Fact]
    public void Only_dragged_sizes_become_properties_and_none_gives_no_style()
    {
        Assert.Null(PanelSizes.Style(AppSettings.Default));
        Assert.Equal("--explorer-width: 340px;", PanelSizes.Style(AppSettings.Default with { ExplorerWidth = 340 }));
        Assert.Equal("--sql-editor-height: 260px;", PanelSizes.Property(PanelSizes.SqlEditorHeight, 260));
        Assert.Null(PanelSizes.Property(PanelSizes.LinqEditorHeight, null));
        Assert.Null(PanelSizes.Property(PanelSizes.LinqEditorHeight, -5)); // a hand-edited workspace file
    }

    [Theory]
    [InlineData(340, "width: 340px;")]
    [InlineData(null, null)] // the default: no attribute, so a double click drops the width dom.js set while dragging
    [InlineData(0, null)]
    [InlineData(-5, null)] // a hand-edited settings file
    public void A_panel_width_is_a_style_only_when_dragged(int? pixels, string? expected) =>
        Assert.Equal(expected, PanelSizes.Width(pixels));
}
