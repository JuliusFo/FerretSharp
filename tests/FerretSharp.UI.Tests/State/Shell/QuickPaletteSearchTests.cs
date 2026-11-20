using FerretSharp.Core.Schema;
using FerretSharp.Core.Settings;
using FerretSharp.Core.Workspaces;
using FerretSharp.UI.State;

namespace FerretSharp.UI.Tests.State;

/// <summary>One box for the quick open and the command palette (WP-38): the prefix decides what it searches.</summary>
public sealed class QuickPaletteSearchTests
{
    private static readonly ShellActionContext Ready = new(
        InExplorer: true,
        Connected: true,
        ActiveTab: new TableTab(Guid.NewGuid(), new TableSummary("APP", "KUNDEN", TableKind.Table)),
        Writable: false,
        LinkedProject: false,
        CanGoBack: false,
        CanGoForward: false,
        PreviousConnection: false);

    private static string? Keys(ShortcutAction action) => new ShortcutMap().Chord(action)?.Label;

    [Theory]
    [InlineData("", false, "")]
    [InlineData("kunde", false, "kunde")]
    [InlineData(">", true, "")]
    [InlineData("> tab schl", true, "tab schl")]
    [InlineData("  >commit ", true, "commit")]
    [InlineData("a>b", false, "a>b")]
    public void The_prefix_switches_to_the_commands(string text, bool commands, string query)
    {
        Assert.Equal(commands, QuickPaletteSearch.IsCommand(text));
        Assert.Equal(query, QuickPaletteSearch.QueryOf(text));
    }

    [Fact]
    public void Opened_as_command_palette_it_starts_with_the_prefix()
    {
        Assert.Equal(">", new PaletteRequest(commands: true).InitialText);
        Assert.Equal("", new PaletteRequest(commands: false).InitialText);
    }

    [Fact]
    public void Without_a_query_all_actions_of_the_palette() =>
        Assert.Equal(ShellActions.InPalette.Select(d => d.Action), QuickPaletteSearch.Commands("", Ready, Keys).Select(c => c.Definition.Action));

    [Fact]
    public void Actions_are_found_by_every_word_of_their_label()
    {
        var found = QuickPaletteSearch.Commands("tab schl", Ready, Keys).Select(c => c.Definition.Action);

        Assert.Equal([ShortcutAction.CloseTab, ShortcutAction.CloseOtherTabs, ShortcutAction.CloseAllTabs], found);
    }

    [Fact]
    public void Actions_are_found_by_group_and_key()
    {
        Assert.Contains(ShortcutAction.Commit, QuickPaletteSearch.Commands("editieren", Ready, Keys).Select(c => c.Definition.Action));
        Assert.Equal([ShortcutAction.ToggleChanges], QuickPaletteSearch.Commands("shift+u", Ready, Keys).Select(c => c.Definition.Action));
    }

    [Fact]
    public void A_found_action_carries_its_key_and_why_it_cannot_run()
    {
        var flush = Assert.Single(QuickPaletteSearch.Commands("ausstehende schreiben", Ready, Keys));

        Assert.Equal("Ctrl+S", flush.Key);
        Assert.Equal("Der Workspace ist schreibgeschützt.", flush.Unavailable);
    }

    /// <summary>Review of WP-38: the key of the mode shown closes the box, not the key it was opened with.</summary>
    [Fact]
    public void The_key_of_the_mode_shown_closes_the_box()
    {
        var shell = new ShellState();

        shell.TogglePalette(commands: false);
        shell.Palette!.ShowsCommands = true; // "> commit" typed
        shell.TogglePalette(commands: true);
        Assert.Null(shell.Palette);

        shell.TogglePalette(commands: true);
        shell.Palette!.ShowsCommands = false; // the prefix deleted
        shell.TogglePalette(commands: false);
        Assert.Null(shell.Palette);
        shell.TogglePalette(commands: true);
        shell.Palette!.ShowsCommands = false;
        shell.TogglePalette(commands: true);
        Assert.Equal(">", shell.Palette!.InitialText); // back to the commands
    }

    [Fact]
    public void The_same_key_closes_the_box_the_other_switches_and_the_switcher_takes_its_place()
    {
        var shell = new ShellState();

        shell.TogglePalette(commands: false);
        var quick = Assert.IsType<PaletteRequest>(shell.Palette);
        shell.TogglePalette(commands: true);
        Assert.NotSame(quick, shell.Palette);
        Assert.Equal(">", shell.Palette!.InitialText);
        shell.TogglePalette(commands: true);
        Assert.Null(shell.Palette);

        shell.TogglePalette(commands: false);
        shell.OpenSwitcher();
        Assert.Null(shell.Palette);
        shell.TogglePalette(commands: false);
        Assert.False(shell.SwitcherOpen);
    }

    private static readonly TableSummary Auftrag = new("APP", "AUFTRAG", TableKind.Table);
    private static readonly TableSummary Kunden = new("APP", "KUNDEN", TableKind.Table);
    private static readonly PlSqlObjectSummary Rechnung = new("APP", "PKG_RECHNUNG", PlSqlKind.Package, "VALID");
    private static readonly PlSqlObjectSummary Trigger = new("APP", "AUFTRAG", PlSqlKind.Trigger, "VALID");

    /// <summary>WP-91: the favorites the schema has, tables and PL/SQL together by name, ten at most.</summary>
    [Fact]
    public void The_empty_box_offers_the_favorites_by_name()
    {
        TableSummary[] many = [.. Enumerable.Range(0, 12).Select(i => new TableSummary("APP", $"T{i:00}", TableKind.Table))];

        var favorites = QuickPaletteSearch.Favorites([Kunden, Auftrag, .. many], [Rechnung, Trigger], t => t != Kunden && !t.Name.StartsWith('T'), u => u == Rechnung);
        var limited = QuickPaletteSearch.Favorites(many, [], _ => true, _ => false);

        Assert.Equal(["AUFTRAG", "PKG_RECHNUNG"], favorites.Select(o => o.DisplayName));
        Assert.Equal(Rechnung, favorites[1].Unit);
        Assert.Equal(QuickPaletteSearch.StartLimit, limited.Count);
    }

    /// <summary>WP-91: the objects opened last that the schema still has, newest first, without the open ones; a trigger is found as one.</summary>
    [Fact]
    public void The_empty_box_offers_the_objects_opened_last_that_are_there_and_not_open()
    {
        var now = DateTimeOffset.Now;
        RecentObject[] recent =
        [
            RecentObject.Of(Trigger, now), RecentObject.Of(Kunden, now), new(SchemaObjectKind.Table, "APP", "GELOESCHT", now),
            RecentObject.Of(Auftrag, now), RecentObject.Of(Rechnung, now),
        ];

        var offered = QuickPaletteSearch.Recent(recent, t => new[] { Auftrag, Kunden }.FirstOrDefault(x => x.Ref == t),
            u => new[] { Rechnung, Trigger }.FirstOrDefault(x => x.Ref == u), o => o.Table == Kunden);

        Assert.Equal([Trigger, null, Rechnung], offered.Select(o => o.Unit));
        Assert.Equal(Auftrag, offered[1].Table);
    }
}
