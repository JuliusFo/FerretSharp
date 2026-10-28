using FerretSharp.UI.State;

namespace FerretSharp.UI.Tests.State;

/// <summary>WP-66: the settings open at one section (a group's audit template) – once, also while they are shown.</summary>
public sealed class SettingsPartRequestTests
{
    [Fact]
    public void A_requested_section_is_taken_once_and_announced()
    {
        var shell = new ShellState();
        var changed = 0;
        shell.Changed += () => changed++;

        shell.ShowSettings(SettingsPart.Audit);

        Assert.Equal(ShellPage.Settings, shell.Page);
        Assert.True(changed > 0); // the shown page hears it (review of WP-66)
        Assert.Equal(SettingsPart.Audit, shell.TakeSettingsPart());
        Assert.Null(shell.TakeSettingsPart());
    }

    /// <summary>Review of WP-66: a request nobody took must not move the next plain visit.</summary>
    [Fact]
    public void Opening_the_settings_plainly_drops_an_unread_request()
    {
        var shell = new ShellState();
        shell.ShowSettings(SettingsPart.Audit);

        shell.ShowSettings();

        Assert.Null(shell.TakeSettingsPart());
    }
}
