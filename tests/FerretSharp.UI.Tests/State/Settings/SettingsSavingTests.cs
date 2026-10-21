using FerretSharp.UI.State;

namespace FerretSharp.UI.Tests.State;

/// <summary>R4b: one helper for saving a setting from the UI, with the same notice everywhere.</summary>
public sealed class SettingsSavingTests
{
    [Fact]
    public async Task A_setting_that_could_not_be_written_says_so_and_stays_in_effect()
    {
        var shell = new ShellState();

        Assert.False(await shell.TrySaveSettingsAsync(() => Task.FromException(new IOException("Datenträger voll"))));

        Assert.Equal("Einstellung angewendet, aber nicht gespeichert: Datenträger voll", shell.Notice?.Text);
    }

    [Fact]
    public async Task A_saved_setting_says_nothing_and_other_failures_are_bugs()
    {
        var shell = new ShellState();

        Assert.True(await shell.TrySaveSettingsAsync(() => Task.CompletedTask));
        Assert.Null(shell.Notice);
        await Assert.ThrowsAsync<InvalidOperationException>(() => shell.TrySaveSettingsAsync(() => throw new InvalidOperationException()));
    }
}
