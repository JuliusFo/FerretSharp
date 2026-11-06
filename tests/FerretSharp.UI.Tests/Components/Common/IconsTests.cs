using FerretSharp.UI.Components;
using FerretSharp.UI.State;

namespace FerretSharp.UI.Tests.Components.Common;

/// <summary>The sections of the settings page each show an icon of their own (review of the settings icons).</summary>
public sealed class IconsTests
{
    [Fact]
    public void Every_settings_section_has_an_icon_of_its_own()
    {
        var icons = Enum.GetValues<SettingsPart>().Select(part => Icons.For(part).Value).ToList();

        Assert.Equal(icons.Count, icons.Distinct().Count());
    }
}
