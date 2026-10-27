using FerretSharp.Core.ClrModel;
using FerretSharp.UI.State;

namespace FerretSharp.UI.Tests.State;

/// <summary>Where a linked model file (WP-64) comes from, as the model page and the status bar say it.</summary>
public sealed class ModelFileTextTests
{
    private static readonly ModelFileOrigin Origin = new("Shop.Data", "Release", null, new DateTimeOffset(2026, 10, 12, 9, 30, 0, TimeSpan.Zero),
        "release", "a1b2c3d", "3.53.0");

    [Fact]
    public void The_source_names_project_build_and_git()
    {
        Assert.Equal("aus Shop.Data (Release) · release @ a1b2c3d", ModelFileText.Source(Origin));
        Assert.Equal("aus Shop.Data (Debug, ShopContext)", ModelFileText.Source(Origin with { Configuration = "Debug", ContextType = "ShopContext", Branch = null, Commit = null }));
        using (UiCulture.Use("en"))
        {
            Assert.Equal("from Shop.Data (Release) · release @ a1b2c3d", ModelFileText.Source(Origin));
        }
    }

    [Fact]
    public void A_detached_head_is_named_once()
    {
        var detached = Origin with { Branch = "a1b2c3d" };

        Assert.EndsWith(" · a1b2c3d", ModelFileText.Short(detached));
        Assert.StartsWith(Origin.ExportedAt.ToLocalTime().ToString("dd.MM.yyyy", System.Globalization.CultureInfo.CurrentCulture), ModelFileText.Short(Origin));
        Assert.EndsWith(" · release @ a1b2c3d", ModelFileText.Short(Origin));
    }
}
