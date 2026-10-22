using FerretSharp.Core.Settings;

namespace FerretSharp.Core.Tests.Settings;

public sealed class ShortcutMapTests
{
    private static KeyChord Chord(string text) => KeyChord.Parse(text)!;

    /// <summary>Until WP-25 these were constants in Shell.razor, grid.js and RowFormPanel – the defaults keep them.</summary>
    [Fact]
    public void Defaults_are_the_keys_before_wp_25()
    {
        var map = new ShortcutMap();

        Assert.Equal(
            // ctrl+shift+u since WP-30 (change overview)
            ["ctrl+shift+o", "alt+o", "alt+arrowleft", "alt+arrowright", "ctrl+enter", "f5", "ctrl+f", "ctrl+s", "ctrl+shift+enter", "ctrl+shift+u", "ctrl+shift+q", "ctrl+shift+l", "alt+x"],
            map.GlobalCombos);
        Assert.Equal(Chord("alt+enter"), map.Chord(ShortcutAction.ToggleForm));
        Assert.Equal(Chord("alt+arrowup"), map.Chord(ShortcutAction.FormPrevious));
        Assert.Equal(Chord("alt+arrowdown"), map.Chord(ShortcutAction.FormNext));
        Assert.Equal(Chord("ctrl+z"), map.Chord(ShortcutAction.UndoEdit)); // WP-30, only in the grid
        Assert.Equal(Chord("ctrl+y"), map.Chord(ShortcutAction.RedoEdit));
        Assert.Null(map.Chord(ShortcutAction.CloseTab)); // after 3.20: new actions start without a key
        Assert.Null(map.Chord(ShortcutAction.CloseOtherTabs));
        Assert.Null(map.Chord(ShortcutAction.CloseAllTabs));
        Assert.Null(map.Chord(ShortcutAction.QuickOpen)); // WP-38
        Assert.Null(map.Chord(ShortcutAction.CommandPalette));
        Assert.False(map.HasChanges);
    }

    [Fact]
    public void Every_action_has_a_valid_unique_default()
    {
        var map = new ShortcutMap();

        Assert.Equal(Enum.GetValues<ShortcutAction>(), ShortcutMap.Definitions.Select(d => d.Action));
        var keyed = ShortcutMap.Definitions.Where(d => d.DefaultChord is not null).ToList();
        Assert.All(keyed, d =>
        {
            var check = map.Check(d.Action, d.DefaultChord!);
            Assert.Null(check.Error);
            Assert.Null(check.Conflict);
        });
        Assert.Equal(keyed.Count, keyed.Select(d => d.Default).Distinct().Count());
    }

    /// <summary>The definitions as if Alt+X's action had come after 3.20: new actions start without a key.</summary>
    private static IReadOnlyList<ShortcutDefinition> RunScriptUnbound =>
        [.. ShortcutMap.Definitions.Select(d => d.Action == ShortcutAction.RunScript ? d with { Default = null } : d)];

    [Fact]
    public void An_action_without_a_default_key_is_unchanged_until_the_user_gives_it_one()
    {
        var map = new ShortcutMap(RunScriptUnbound);

        Assert.Null(map.Chord(ShortcutAction.RunScript));
        Assert.True(map.IsDefault(ShortcutAction.RunScript));
        Assert.False(map.HasChanges);
        Assert.Null(map.ActionFor("alt+x", ShortcutScope.Global));
        Assert.DoesNotContain("alt+x", map.GlobalCombos);
    }

    [Fact]
    public void A_key_for_an_action_without_a_default_is_stored_and_removed_again()
    {
        var overrides = new ShortcutMap(RunScriptUnbound).With(ShortcutAction.RunScript, Chord("ctrl+k"));
        var map = new ShortcutMap(RunScriptUnbound, overrides);

        Assert.Equal(SettingsMap.From(new Dictionary<string, string> { ["RunScript"] = "ctrl+k" }), overrides);
        Assert.Equal(ShortcutAction.RunScript, map.ActionFor("ctrl+k", ShortcutScope.Global));
        Assert.False(map.IsDefault(ShortcutAction.RunScript));
        Assert.Empty(map.With(ShortcutAction.RunScript, null)); // no key is its default: nothing to store
        Assert.Empty(map.Without(ShortcutAction.RunScript));
    }

    [Fact]
    public void A_changed_shortcut_replaces_the_default()
    {
        var map = new ShortcutMap(new ShortcutMap().With(ShortcutAction.Flush, Chord("ctrl+shift+s")));

        Assert.Equal(ShortcutAction.Flush, map.ActionFor("ctrl+shift+s", ShortcutScope.Global));
        Assert.Null(map.ActionFor("ctrl+s", ShortcutScope.Global));
        Assert.Contains("ctrl+shift+s", map.GlobalCombos);
        Assert.Equal(SettingsMap.From(new Dictionary<string, string> { ["Flush"] = "ctrl+shift+s" }), map.Overrides);
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

    /// <summary>
    /// R4a: Refresh moved to F6, Find column got F5, then Refresh was reset – both had F5, and Find column did nothing (the
    /// first definition wins). The reset now takes the key back like setting it.
    /// </summary>
    [Fact]
    public void A_reset_takes_the_default_key_back_from_the_action_that_has_it_meanwhile()
    {
        var map = new ShortcutMap(new ShortcutMap().With(ShortcutAction.Refresh, Chord("f6")));
        map = new ShortcutMap(map.With(ShortcutAction.FindColumn, Chord("f5")));
        Assert.Equal(ShortcutAction.FindColumn, map.Check(ShortcutAction.Refresh, Chord("f5")).Conflict); // what the page asks first

        var reset = new ShortcutMap(map.Without(ShortcutAction.Refresh));

        Assert.Equal(Chord("f5"), reset.Chord(ShortcutAction.Refresh));
        Assert.Null(reset.Chord(ShortcutAction.FindColumn));
        Assert.Equal(ShortcutAction.Refresh, reset.ActionFor("f5", ShortcutScope.Global));
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
    public void A_grid_action_never_gets_ctrl_c()
    {
        Assert.NotNull(new ShortcutMap().Check(ShortcutAction.CopyColumnValues, Chord("ctrl+c")).Error);
        Assert.NotNull(new ShortcutMap().Check(ShortcutAction.ToggleForm, Chord("ctrl+c")).Error);
        Assert.Null(new ShortcutMap().Check(ShortcutAction.Flush, Chord("ctrl+c")).Error); // global: before the grid, with a warning
    }

    [Theory]
    [InlineData(ShortcutAction.CopyColumnValues, "ctrl+a", true)] // select all rows
    [InlineData(ShortcutAction.CopyColumnValues, "ctrl+space", true)]
    [InlineData(ShortcutAction.CopyColumnValues, "ctrl+shift+arrowdown", true)]
    [InlineData(ShortcutAction.CopyColumnValues, "ctrl+arrowdown", true)] // review 3.44.0, A34: to the last row
    [InlineData(ShortcutAction.CopyColumnValues, "ctrl+end", true)]
    [InlineData(ShortcutAction.CopyColumnValues, "ctrl+d", true)] // WP-59: fill down
    [InlineData(ShortcutAction.CopyColumnValues, "ctrl+shift+k", false)]
    [InlineData(ShortcutAction.FormNext, "ctrl+a", false)] // only in the form
    public void Warns_when_the_grid_loses_a_key(ShortcutAction action, string text, bool warns) =>
        Assert.Equal(warns, new ShortcutMap().Check(action, Chord(text)).Warning is not null);

    [Fact]
    public void The_fixed_keys_list_the_grid_selection_keys()
    {
        var keys = ShortcutMap.Fixed.ToDictionary(f => f.Label, f => f.Keys);

        Assert.Equal("Shift+↑, Shift+↓, Shift+Bild↑, Shift+Bild↓", keys["Markierung erweitern (die Pfeiltasten allein verschieben sie)"]);
        Assert.Equal("Shift+Pos1, Shift+Ende, Ctrl+Shift+↑, Ctrl+Shift+↓", keys["Bis zur ersten oder letzten Zeile markieren"]);
        Assert.Equal("Shift+←, Shift+→", keys["Zellen ab der fokussierten markieren (danach erweitern Shift+↑ ↓ und Shift+Bild↑/↓; Ziehen mit der Maus geht auch)"]);
        Assert.Equal("Leertaste", keys["Fokussierte Zeile markieren oder Markierung aufheben"]);
        Assert.Equal("Ctrl+D", keys["Markierte Zellen mit den Werten der obersten Zeile nach unten ausfüllen"]); // WP-59
        Assert.Equal("Ctrl+A", keys["Alle Zeilen markieren"]);
        Assert.Equal("Esc", keys["Markierung aufheben"]);
    }

    [Fact]
    public void Removed_unknown_and_broken_entries_are_handled()
    {
        var map = new ShortcutMap(SettingsMap.From(new Dictionary<string, string>
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
        Assert.Equal(SettingsMap.From(new Dictionary<string, string> { ["SomethingNewer"] = "ctrl+k" }), map.Reset());
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
