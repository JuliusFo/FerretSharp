using FerretSharp.Core.Settings;
using FerretSharp.UI.State;

namespace FerretSharp.UI.Tests.State;

/// <summary>WP-34: what the settings search sees, and what the page shows for a query.</summary>
public sealed class SettingsCatalogTests
{
    private static readonly ShortcutMap Map = new();

    private static SettingsFilter Filter(string query, bool installed = true) =>
        new(new SettingsSearch(query), SettingsCatalog.Entries(Map, installed));

    [Fact]
    public void Every_row_of_the_page_is_in_the_catalog_once_update_checks_only_when_installed()
    {
        var installed = SettingsCatalog.Entries(Map, installed: true).Select(e => e.Id).ToList();
        var notInstalled = SettingsCatalog.Entries(Map, installed: false).Select(e => e.Id).ToList();

        Assert.All(Enum.GetValues<SettingKey>(), key => Assert.Single(installed, SettingsCatalog.IdOf(key)));
        SettingKey[] onlyInstalled = [SettingKey.CheckForUpdates, SettingKey.UpdateCheckInterval];
        Assert.All(Enum.GetValues<SettingKey>().Except(onlyInstalled), key => Assert.Single(notInstalled, SettingsCatalog.IdOf(key)));
        Assert.All(onlyInstalled, key => Assert.DoesNotContain(SettingsCatalog.IdOf(key), notInstalled));
        Assert.Equal(installed.Count, installed.Distinct().Count()); // shortcut and fixed key ids are unique too
        Assert.All(Enum.GetValues<ShortcutAction>(), action => Assert.Contains(SettingsCatalog.IdOf(action), installed));
    }

    [Fact]
    public void Without_a_query_the_selected_section_shows_with_all_rows()
    {
        var filter = Filter("");

        Assert.True(filter.ShowsSection(SettingsPart.Connection, selected: SettingsPart.Connection));
        Assert.False(filter.ShowsSection(SettingsPart.Appearance, selected: SettingsPart.Connection));
        Assert.True(filter.Shows(SettingKey.Language));
        Assert.True(filter.ShowsGroup("anything"));
        Assert.False(filter.IsEmpty);
    }

    [Fact]
    public void A_query_shows_every_section_with_a_matching_row_and_only_those_rows()
    {
        using var _ = UiCulture.Use("de");
        var filter = Filter("sperr");

        Assert.True(filter.ShowsSection(SettingsPart.Connection, selected: SettingsPart.Appearance));
        Assert.False(filter.ShowsSection(SettingsPart.Appearance, selected: SettingsPart.Appearance));
        Assert.True(filter.Shows(SettingKey.LockWait));
        Assert.False(filter.Shows(SettingKey.KeepAlive));
    }

    /// <summary>R6b (B9): the sections of the page hide their rows by this class.</summary>
    [Fact]
    public void A_row_that_does_not_match_gets_the_class_that_hides_it()
    {
        using var _ = UiCulture.Use("de");
        var filter = Filter("sperr");

        Assert.Equal("", filter.HideClass(SettingKey.LockWait));
        Assert.Equal("no-match", filter.HideClass(SettingKey.KeepAlive));
        Assert.Equal("", Filter("").HideClass(SettingKey.KeepAlive));
    }

    [Fact]
    public void Code_shown_in_hints_and_keys_of_shortcuts_are_found()
    {
        Assert.True(Filter("dotnet-stack").Shows(SettingKey.DiagnoseUiStalls));
        Assert.True(Filter("idle_time").Shows(SettingKey.KeepAlive));
        Assert.True(Filter("PetersPizza").Shows(SettingKey.ShowDisplayNames));

        var byKey = Filter("ctrl+s");
        Assert.True(byKey.Shows(SettingsCatalog.IdOf(ShortcutAction.Flush)));
        Assert.True(byKey.ShowsSection(SettingsPart.Shortcuts, selected: SettingsPart.Appearance));
        Assert.True(byKey.ShowsGroup(ShortcutMap.Definition(ShortcutAction.Flush).Group));
        Assert.False(byKey.Shows(SettingsCatalog.IdOf(ShortcutAction.Refresh))); // F5
    }

    [Fact]
    public void Without_installation_the_updates_section_shows_only_for_what_it_shows()
    {
        using var _ = UiCulture.Use("de");

        // "Beim Start prüfen" is not on the page of a FerretSharp that was not installed: no empty section for it.
        Assert.False(Filter("beim start", installed: false).ShowsSection(SettingsPart.Updates, selected: SettingsPart.Appearance));
        Assert.True(Filter("beim start", installed: true).ShowsSection(SettingsPart.Updates, selected: SettingsPart.Appearance));

        // The version row is found by the hint the page shows.
        Assert.True(Filter("nicht installiert", installed: false).Shows(SettingKey.Version));
        Assert.False(Filter("nicht installiert", installed: true).Shows(SettingKey.Version));
    }

    [Fact]
    public void The_data_view_is_found_by_the_header_lines_and_the_sql_of_a_filter()
    {
        using var _ = UiCulture.Use("de");

        var notNull = Filter("not null");
        Assert.True(notNull.ShowsSection(SettingsPart.DataView, selected: SettingsPart.Appearance));
        Assert.True(notNull.Shows(SettingKey.HeaderNotNull));
        Assert.True(Filter("spaltenkopf").Shows(SettingKey.HeaderTypes));
        Assert.True(Filter("t.*").Shows(SettingKey.FilterSql));
        Assert.True(Filter("rowid").Shows(SettingKey.FilterSql));
        Assert.True(Filter("datenansicht").ShowsSection(SettingsPart.DataView, selected: SettingsPart.Appearance));
    }

    [Fact]
    public void A_query_without_any_match_is_empty() => Assert.True(Filter("xyzzy").IsEmpty);

    /// <summary>WP-66: the keys of connections are found by name, group and key, under "Connections".</summary>
    [Fact]
    public void Keys_of_connections_are_searched_by_name_group_and_key()
    {
        var erp = new FerretSharp.Core.Connections.ConnectionProfile(
            Guid.NewGuid(), "ERP Dev", FerretSharp.Core.Connections.ConnectionKind.Dev,
            new FerretSharp.Core.Connections.HostPortAddress("db01", 1521, "ERP", null), "app", null, false, "Warenwirtschaft");
        var map = new ShortcutMap(connections: SettingsMap.From([new(erp.Id.ToString(), "alt+1")]));
        var entries = SettingsCatalog.Entries(map, installed: true, [erp]);

        Assert.Single(entries, e => e.Id == SettingsCatalog.IdOf(erp.Id));
        foreach (var query in new[] { "erp dev", "warenwirtschaft", "alt+1" })
        {
            var filter = new SettingsFilter(new SettingsSearch(query), entries);
            Assert.True(filter.Shows(SettingsCatalog.IdOf(erp.Id)), query);
            Assert.True(filter.ShowsGroup(FerretSharp.UI.Resources.SettingsViewText.Shortcuts_ConnectionsGroup), query);
        }

        Assert.Empty(SettingsCatalog.ConnectionKeys(new ShortcutMap(), [erp]));
    }
}
