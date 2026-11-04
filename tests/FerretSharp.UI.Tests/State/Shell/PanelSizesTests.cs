using FerretSharp.Core.Settings;
using FerretSharp.UI.State;

namespace FerretSharp.UI.Tests.State;

/// <summary>The dragged sizes as CSS custom properties on the app root: only those set, the stylesheet keeps the rest.</summary>
public sealed class PanelSizesTests
{
    [Fact]
    public void Only_dragged_sizes_become_properties_and_none_gives_no_style()
    {
        Assert.Null(PanelSizes.Style(AppSettings.Default));
        Assert.Equal("--explorer-width: 340px; --linq-editor-height: 1200px;",
            PanelSizes.Style(AppSettings.Default with { ExplorerWidth = 340, LinqEditorHeight = 1200 }));
        Assert.Equal("--sql-editor-height: 260px;", PanelSizes.Style(AppSettings.Default with { SqlEditorHeight = 260 }));
    }
}
