using FerretSharp.Core.Settings;

namespace FerretSharp.Core.Tests.Settings;

public sealed class KeyChordTests
{
    [Theory]
    [InlineData("ctrl+shift+o", "Ctrl+Shift+O", "Ctrl Shift O")]
    [InlineData("ctrl+enter", "Ctrl+Enter", "Ctrl ↵")]
    [InlineData("alt+arrowleft", "Alt+←", "Alt ←")]
    [InlineData("f5", "F5", "F5")]
    [InlineData("ctrl+space", "Ctrl+Leertaste", "Ctrl Leertaste")]
    [InlineData("ctrl+plus", "Ctrl++", "Ctrl +")]
    [InlineData("SHIFT+CTRL+O", "Ctrl+Shift+O", "Ctrl Shift O")] // any order and case in, one form out
    public void Parses_and_labels(string text, string label, string kbd)
    {
        var chord = KeyChord.Parse(text)!;

        Assert.Equal(label, chord.Label);
        Assert.Equal(kbd, chord.KbdLabel);
        Assert.Equal(chord, KeyChord.Parse(chord.Text));
    }

    [Theory]
    [InlineData("")]
    [InlineData("ctrl+")]
    [InlineData("ctrl+shift")] // only modifiers
    [InlineData("ctrl+ctrl+o")]
    [InlineData("hyper+o")]
    [InlineData("ctrl++")]
    public void Refuses_what_is_no_key_combination(string text) => Assert.Null(KeyChord.Parse(text));

    [Theory]
    [InlineData(" ", "space")]
    [InlineData("+", "plus")]
    [InlineData("O", "o")]
    [InlineData("ArrowLeft", "arrowleft")]
    public void Takes_keys_from_events_like_shortcuts_js(string key, string expected) =>
        Assert.Equal(expected, KeyChord.FromEvent(key, true, false, false)!.Key);

    [Fact]
    public void A_modifier_alone_is_no_chord() => Assert.Null(KeyChord.FromEvent("Control", true, false, false));
}

public sealed class ShortcutMapTests
{
    private static KeyChord Chord(string text) => KeyChord.Parse(text)!;

    /// <summary>Until WP-25 these were constants in Shell.razor, grid.js and RowFormPanel – the defaults keep them (plus Ctrl+W, 3.19).</summary>
    [Fact]
    public void Defaults_are_the_keys_before_wp_25()
    {
        var map = new ShortcutMap();

        Assert.Equal(
            ["ctrl+shift+o", "alt+o", "alt+arrowleft", "alt+arrowright", "ctrl+w", "ctrl+enter", "f5", "ctrl+f", "ctrl+s", "ctrl+shift+enter", "ctrl+shift+q", "ctrl+shift+l", "alt+x"],
            map.GlobalCombos);
        Assert.Equal(Chord("alt+enter"), map.Chord(ShortcutAction.ToggleForm));
        Assert.Equal(Chord("alt+arrowup"), map.Chord(ShortcutAction.FormPrevious));
        Assert.Equal(Chord("alt+arrowdown"), map.Chord(ShortcutAction.FormNext));
        Assert.False(map.HasChanges);
    }

    [Fact]
    public void Ctrl_w_closes_the_tab_everywhere() =>
        Assert.Equal(ShortcutAction.CloseTab, new ShortcutMap().ActionFor("ctrl+w", ShortcutScope.Global));

    [Fact]
    public void Every_action_has_a_valid_unique_default()
    {
        var map = new ShortcutMap();

        Assert.Equal(Enum.GetValues<ShortcutAction>(), ShortcutMap.Definitions.Select(d => d.Action));
        Assert.All(ShortcutMap.Definitions, d =>
        {
            var check = map.Check(d.Action, d.DefaultChord);
            Assert.Null(check.Error);
            Assert.Null(check.Conflict);
        });
        Assert.Equal(ShortcutMap.Definitions.Count, ShortcutMap.Definitions.Select(d => d.Default).Distinct().Count());
    }

    [Fact]
    public void A_changed_shortcut_replaces_the_default()
    {
        var map = new ShortcutMap(new ShortcutMap().With(ShortcutAction.Flush, Chord("ctrl+shift+s")));

        Assert.Equal(ShortcutAction.Flush, map.ActionFor("ctrl+shift+s", ShortcutScope.Global));
        Assert.Null(map.ActionFor("ctrl+s", ShortcutScope.Global));
        Assert.Contains("ctrl+shift+s", map.GlobalCombos);
        Assert.Equal(ShortcutOverrides.From(new Dictionary<string, string> { ["Flush"] = "ctrl+shift+s" }), map.Overrides);
    }

    [Fact]
    public void Local_shortcuts_are_found_only_where_they_are_caught()
    {
        var map = new ShortcutMap();

        Assert.Null(map.ActionFor("alt+enter", ShortcutScope.Global));
        Assert.Equal(ShortcutAction.ToggleForm, map.ActionFor("alt+enter", ShortcutScope.GridAndForm, ShortcutScope.Form));
        Assert.Equal(ShortcutAction.FormNext, map.ActionFor("alt+arrowdown", ShortcutScope.GridAndForm, ShortcutScope.Form));
        Assert.DoesNotContain("alt+enter", map.GlobalCombos);
    }

    [Fact]
    public void Taking_over_a_used_key_leaves_the_other_action_without_one()
    {
        var map = new ShortcutMap();
        Assert.Equal(ShortcutAction.Refresh, map.Check(ShortcutAction.Flush, Chord("f5")).Conflict);

        var changed = new ShortcutMap(map.With(ShortcutAction.Flush, Chord("f5")));

        Assert.Equal(ShortcutAction.Flush, changed.ActionFor("f5", ShortcutScope.Global));
        Assert.Null(changed.Chord(ShortcutAction.Refresh));
        Assert.Equal("", changed.Overrides["Refresh"]);
    }

    [Fact]
    public void Setting_the_default_again_removes_the_entry()
    {
        var changed = new ShortcutMap(new ShortcutMap().With(ShortcutAction.Flush, Chord("ctrl+shift+s")));

        Assert.Empty(changed.With(ShortcutAction.Flush, Chord("ctrl+s")));
        Assert.Empty(changed.Without(ShortcutAction.Flush));
    }

    [Theory]
    [InlineData("alt+f4")]
    [InlineData("f12")]
    [InlineData("ctrl+alt+q")] // AltGr+Q is @ on a German keyboard
    [InlineData("o")]
    [InlineData("shift+o")]
    [InlineData("enter")]
    public void Refuses_keys_windows_takes_or_typing_needs(string text) =>
        Assert.NotNull(new ShortcutMap().Check(ShortcutAction.Flush, Chord(text)).Error);

    [Theory]
    [InlineData(ShortcutAction.Flush, "ctrl+z", true)] // Monaco's undo
    [InlineData(ShortcutAction.Flush, "ctrl+space", true)]
    [InlineData(ShortcutAction.Flush, "alt+shift+s", true)] // keyboard layout switch
    [InlineData(ShortcutAction.FormNext, "alt+arrowdown", false)] // only in the form: Monaco keeps its key
    [InlineData(ShortcutAction.Flush, "f9", false)]
    public void Warns_when_the_editors_lose_a_key(ShortcutAction action, string text, bool warns) =>
        Assert.Equal(warns, new ShortcutMap().Check(action, Chord(text)).Warning is not null);

    [Fact]
    public void Removed_unknown_and_broken_entries_are_handled()
    {
        var map = new ShortcutMap(ShortcutOverrides.From(new Dictionary<string, string>
        {
            ["NewLinq"] = "",
            ["SomethingNewer"] = "ctrl+k",
            ["Flush"] = "nonsense+",
            ["Commit"] = "alt+f4",
        }));

        Assert.Null(map.Chord(ShortcutAction.NewLinq));
        Assert.DoesNotContain("ctrl+shift+l", map.GlobalCombos);
        Assert.Equal(Chord("ctrl+s"), map.Chord(ShortcutAction.Flush)); // unreadable: default
        Assert.Equal(Chord("ctrl+shift+enter"), map.Chord(ShortcutAction.Commit)); // reserved: default
        Assert.Equal("ctrl+k", map.With(ShortcutAction.Refresh, Chord("f6"))["SomethingNewer"]);
        Assert.Equal(ShortcutOverrides.From(new Dictionary<string, string> { ["SomethingNewer"] = "ctrl+k" }), map.Reset());
    }

    [Fact]
    public async Task Shortcuts_round_trip_through_the_settings_file()
    {
        using var folder = new TestFolder();
        var store = new SettingsStore(folder.Combine("settings.json"));
        var overrides = new ShortcutMap().With(ShortcutAction.RunScript, Chord("ctrl+shift+x"));

        await store.SaveAsync(new AppSettings { Shortcuts = overrides }, TestContext.Current.CancellationToken);
        var loaded = await store.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(overrides, loaded.Shortcuts);
        Assert.Contains("\"RunScript\": \"ctrl+shift+x\"", await File.ReadAllTextAsync(store.FilePath, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Files_from_before_wp_25_have_no_changes()
    {
        using var folder = new TestFolder();
        var store = new SettingsStore(folder.Combine("settings.json"));
        await File.WriteAllTextAsync(store.FilePath, """{ "version": 1, "settings": { "theme": "dark" } }""", TestContext.Current.CancellationToken);

        Assert.Empty((await store.LoadAsync(TestContext.Current.CancellationToken)).Shortcuts);
    }
}
