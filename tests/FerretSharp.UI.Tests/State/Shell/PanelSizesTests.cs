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
}
