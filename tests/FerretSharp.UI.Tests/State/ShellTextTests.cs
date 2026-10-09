using FerretSharp.Core.Connections;
using FerretSharp.UI.State;

namespace FerretSharp.UI.Tests.State;

/// <summary>Texts of the shell's state classes in both UI languages (WP-29).</summary>
public sealed class ShellTextTests
{
    [Fact]
    public void Connection_kinds_are_named_in_the_UI_language()
    {
        Assert.Equal("Produktion", ConnectionKindInfo.Label(ConnectionKind.Prod));
        Assert.Equal("Sonstige", ConnectionKindInfo.Label(ConnectionKind.Other));

        using (UiCulture.Use("en"))
        {
            Assert.Equal("Production", ConnectionKindInfo.Label(ConnectionKind.Prod));
            Assert.Equal("Development", ConnectionKindInfo.Label(ConnectionKind.Dev));
            Assert.Equal("Other", ConnectionKindInfo.Label(ConnectionKind.Other));
        }

        // The badge is the same in both languages.
        Assert.Equal("PROD", ConnectionKindInfo.Short(ConnectionKind.Prod));
    }

    [Fact]
    public void A_duplicate_is_named_in_the_language_it_was_made_in()
    {
        var original = TestApp.Profile("ERP");

        Assert.Equal("ERP (Kopie)", ConnectionForm.Duplicate(original).Name);
        using (UiCulture.Use("en"))
        {
            Assert.Equal("ERP (copy)", ConnectionForm.Duplicate(original).Name);
        }
    }
}
