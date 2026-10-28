using FerretSharp.Core.Settings;
using FerretSharp.UI.State;
using Microsoft.AspNetCore.Components.Web;

namespace FerretSharp.UI.Tests.State;

/// <summary>The shortcuts in effect follow the settings (WP-25): the Shell re-registers, buttons and tooltips update.</summary>
public sealed class ShortcutServiceTests : IDisposable
{
    private readonly TestFolder _folder = new();
    private readonly AppSettingsService _settings;
    private readonly ShortcutService _shortcuts;

    public ShortcutServiceTests()
    {
        _settings = new AppSettingsService(new SettingsStore(_folder.Combine("settings.json")), AppSettings.Default);
        _shortcuts = new ShortcutService(_settings);
    }

    [Fact]
    public async Task A_change_is_in_effect_at_once_and_announced()
    {
        var changed = 0;
        _shortcuts.Changed += () => changed++;

        await _shortcuts.SetAsync(ShortcutAction.Flush, KeyChord.Parse("ctrl+shift+s"));

        Assert.Equal(1, changed);
        Assert.Equal(" (Ctrl+Shift+S)", _shortcuts.Hint(ShortcutAction.Flush));
        Assert.Contains("ctrl+shift+s", _shortcuts.Map.GlobalCombos);
        Assert.Equal("ctrl+shift+s", _settings.Current.Shortcuts["Flush"]);
    }

    [Fact]
    public async Task Other_settings_do_not_announce_a_change()
    {
        var changed = 0;
        _shortcuts.Changed += () => changed++;

        await _settings.UpdateAsync(s => s with { KeepAlive = false }, TestContext.Current.CancellationToken);

        Assert.Equal(0, changed);
    }

    [Fact]
    public async Task An_action_without_shortcut_shows_none()
    {
        await _shortcuts.SetAsync(ShortcutAction.Refresh, null);

        Assert.Equal("", _shortcuts.Hint(ShortcutAction.Refresh));
        Assert.Null(_shortcuts.Label(ShortcutAction.Refresh));
        Assert.DoesNotContain("f5", _shortcuts.Map.GlobalCombos);

        await _shortcuts.ResetAllAsync();

        Assert.Equal(" (F5)", _shortcuts.Hint(ShortcutAction.Refresh));
    }

    [Fact]
    public async Task The_form_keys_match_key_events_and_go_to_grid_js()
    {
        Assert.True(_shortcuts.Is(ShortcutAction.FormNext, new KeyboardEventArgs { Key = "ArrowDown", AltKey = true }));
        Assert.False(_shortcuts.Is(ShortcutAction.FormNext, new KeyboardEventArgs { Key = "ArrowDown", AltKey = true, ShiftKey = true }));
        Assert.Equal("alt+enter", _shortcuts.LocalCombos["form"]);

        await _shortcuts.SetAsync(ShortcutAction.ToggleForm, KeyChord.Parse("ctrl+shift+f"));

        Assert.True(_shortcuts.Is(ShortcutAction.ToggleForm, new KeyboardEventArgs { Key = "F", CtrlKey = true, ShiftKey = true }));
        Assert.Equal("ctrl+shift+f", _shortcuts.LocalCombos["form"]);
    }

    public void Dispose()
    {
        _shortcuts.Dispose();
        _folder.Dispose();
    }

    private static readonly Guid Erp = Guid.Parse("6f1e1b6e-0000-0000-0000-000000000001");

    /// <summary>WP-66: a connection's key is registered like a global action; taking it moves it, never doubles it.</summary>
    [Fact]
    public async Task A_connection_key_takes_the_key_from_an_action_and_back()
    {
        var changed = 0;
        _shortcuts.Changed += () => changed++;

        await _shortcuts.SetConnectionAsync(Erp, KeyChord.Parse("alt+o")); // the previous connection's

        Assert.Equal(1, changed);
        Assert.Equal("Alt+O", _shortcuts.ConnectionKey(Erp));
        Assert.Null(_shortcuts.Label(ShortcutAction.PreviousConnection));
        Assert.Equal(Erp, _shortcuts.Map.ConnectionFor("alt+o"));

        await _shortcuts.ResetAsync(ShortcutAction.PreviousConnection);

        Assert.Equal("Alt+O", _shortcuts.Label(ShortcutAction.PreviousConnection));
        Assert.Null(_shortcuts.ConnectionKey(Erp));
        Assert.Empty(_settings.Current.ConnectionShortcuts);
    }

    [Fact]
    public async Task Resetting_all_shortcuts_takes_the_defaults_from_connections()
    {
        await _shortcuts.SetConnectionAsync(Erp, KeyChord.Parse("alt+o"));
        await _shortcuts.SetConnectionAsync(Guid.NewGuid(), KeyChord.Parse("alt+2"));

        await _shortcuts.ResetAllAsync();

        Assert.Null(_shortcuts.ConnectionKey(Erp));
        Assert.Single(_settings.Current.ConnectionShortcuts);
    }

    [Fact]
    public async Task Keys_of_connections_that_are_gone_are_dropped()
    {
        var other = Guid.NewGuid();
        await _shortcuts.SetConnectionAsync(Erp, KeyChord.Parse("alt+1"));
        await _shortcuts.SetConnectionAsync(other, KeyChord.Parse("alt+2"));

        await _shortcuts.ForgetConnectionsExceptAsync(new HashSet<Guid> { other });

        Assert.Null(_shortcuts.ConnectionKey(Erp));
        Assert.Equal("Alt+2", _shortcuts.ConnectionKey(other));
    }
}
