using FerretSharp.Core.Connections;
using FerretSharp.Core.Query;
using FerretSharp.Core.Schema;
using FerretSharp.Core.Workspaces;
using FerretSharp.UI.State;
using NSubstitute;

namespace FerretSharp.UI.Tests.State;

/// <summary>Tab operations act on the workspace a tab belongs to, also in a connection in the background (WP-24).</summary>
public sealed class ShellStateTabsTests : IAsyncDisposable
{
    private readonly TestApp _app = new();

    [Fact]
    public async Task Schema_refresh_of_one_connection_leaves_the_tabs_of_another_alone()
    {
        var test = await _app.OpenAsync(TestApp.Profile("Test"));
        var dev = await _app.OpenAsync(TestApp.Profile("Dev"));
        var testTab = AddTab(test, TestApp.Kunden);
        var devTab = AddTab(dev, TestApp.Kunden);

        // Dev's refreshed schema no longer has KUNDEN.
        _app.Shell.RemoveVanishedTabs(dev.Id, Gone());

        Assert.DoesNotContain(devTab, _app.WorkspaceOf(dev).Tabs);
        Assert.Contains(testTab, _app.WorkspaceOf(test).Tabs);
    }

    /// <summary>
    /// Review 3.40.0, A17: a script for a connection whose password was cancelled, which failed or which the user left kept
    /// waiting – and opened hours later when the user connected by hand. Links already behaved so (review of WP-37).
    /// </summary>
    [Fact]
    public async Task A_waiting_script_is_dropped_like_a_waiting_link()
    {
        var test = await _app.OpenAsync(TestApp.Profile("Test"));
        var dev = await _app.OpenAsync(TestApp.Profile("Dev"));

        _app.Shell.OpenSqlIn(test.Id, "SELECT 1 FROM dual");
        _app.Shell.RequestPassword(test.Profile!, returnTo: null);
        _app.Shell.CancelPassword();
        _app.Shell.OpenSqlIn(dev.Id, "SELECT 2 FROM dual"); // shown: opens at once
        _app.Shell.OpenSqlIn(test.Id, "SELECT 3 FROM dual");
        _app.Shell.ReportConnectionFailure(test.Id, new ConnectionError("ORA-12170: TNS:Connect timeout occurred", "ORA-12170"));
        _app.Shell.ShowConnection(test.Id);

        Assert.Empty(_app.WorkspaceOf(test).Tabs.OfType<SqlTab>());
        Assert.Equal(["SELECT 2 FROM dual"], _app.WorkspaceOf(dev).Tabs.OfType<SqlTab>().Select(t => t.Text));
    }

    /// <summary>Review 3.40.0, A5: a refresh closed a tab whose table was gone without a word about its changes.</summary>
    [Fact]
    public async Task A_tab_with_changes_stays_when_its_table_is_gone()
    {
        var scope = await _app.OpenAsync(TestApp.Profile("Test"));
        var tab = _app.AddPendingWork(scope);

        _app.Shell.RemoveVanishedTabs(scope.Id, Gone());

        Assert.Contains(tab, _app.WorkspaceOf(scope).Tabs);
    }

    /// <summary>
    /// Review 3.40.0, A5: a table of another schema that no synonym reaches (opened by an FK jump) is never in the cache –
    /// every DDL and schema refresh closed its tab.
    /// </summary>
    [Fact]
    public async Task A_tab_on_a_table_of_another_schema_stays_after_a_refresh()
    {
        var scope = await _app.OpenAsync(TestApp.Profile("Test"));
        var foreign = AddTab(scope, new TableSummary("STAMMDATEN", "LAENDER", TableKind.Table));

        _app.Shell.RemoveVanishedTabs(scope.Id, Gone());

        Assert.Contains(foreign, _app.WorkspaceOf(scope).Tabs);
    }

    /// <summary>Review 3.40.0, A5: such a tab was dropped on every start; a gone table of the browsed schema still is.</summary>
    [Fact]
    public async Task A_saved_tab_on_a_table_of_another_schema_is_restored_by_name()
    {
        var scope = await _app.OpenAsync(TestApp.Profile("Test"));
        var saved = new Workspace(Guid.NewGuid(), scope.Id, "Gespeichert")
        {
            Tabs =
            [
                new TabState(new TableRef("STAMMDATEN", "LAENDER"), TabMode.Data, [], [], []),
                new TabState(new TableRef(TestApp.Kunden.Owner, "GELOESCHT"), TabMode.Data, [], [], []),
            ],
        };

        _app.Shell.SyncWorkspaces(scope.Id, [saved], saved.Id, scope.Active.Schema!);

        var restored = _app.Shell.AllWorkspaces.Single(w => w.WorkspaceId == saved.Id);
        Assert.Equal([new TableRef("STAMMDATEN", "LAENDER")], restored.Tabs.OfType<TableTab>().Select(t => t.Table.Ref));
    }

    /// <summary>The browsed schema (APP_USER, as TestApp's tables) after a refresh, without any table.</summary>
    private static SchemaCache Gone() => new(Substitute.For<ISchemaReader>(), TestApp.Kunden.Owner);

    /// <summary>Review of WP-38: the quick open shows the tab chosen, also when an FK jump opened a second one of the table.</summary>
    [Fact]
    public async Task Showing_a_tab_shows_that_one_of_two_of_the_same_table()
    {
        await _app.OpenAsync(TestApp.Profile("Test"));
        var first = _app.Shell.OpenTable(TestApp.Kunden)!;
        var second = _app.Shell.OpenFiltered(TestApp.Kunden, [])!;
        _app.Shell.ActivateTab(first);
        _app.Shell.ShowSettings();

        _app.Shell.ShowTab(second);

        Assert.Same(second, _app.Shell.ActiveTab);
        Assert.Equal(ShellPage.Explorer, _app.Shell.Page);
    }

    /// <summary>
    /// WP-70: the copy sits right after the tab it came from (not after the active one) and takes what the grid applied –
    /// rows still being edited stay with the source.
    /// </summary>
    [Fact]
    public async Task A_duplicate_follows_its_tab_with_the_applied_filters_sort_and_view()
    {
        await _app.OpenAsync(TestApp.Profile("Test"));
        var source = _app.Shell.OpenTable(TestApp.Kunden)!;
        var applied = new[] { FilterCondition.Of("NAME", FilterOperator.Contains, "Meier") };
        (source.AppliedFilters, source.Sorts, source.Mode) = (applied, [new SortSpec("NAME", true)], TabMode.Structure);
        source.SetFilterRows([.. applied, FilterCondition.Of("ORT", FilterOperator.Equals, "Kiel")]);
        var sql = _app.Shell.OpenSql()!;

        var copy = _app.Shell.DuplicateTab(source)!;

        Assert.Equal([source, copy, sql], _app.Shell.Tabs.ToList<WorkspaceTab>());
        Assert.Same(copy, _app.Shell.ActiveTab);
        Assert.Equal(applied, copy.AppliedFilters);
        Assert.Equal(["NAME"], copy.FilterRows.Select(r => r.Column));
        Assert.Equal(source.Sorts, copy.Sorts);
        Assert.Equal(TabMode.Structure, copy.Mode);
        Assert.False(copy.Pinned);
    }

    /// <summary>WP-70: the explorer of the tab's connection takes the request; a table it does not list gets a notice instead.</summary>
    [Fact]
    public async Task Show_in_explorer_asks_the_tabs_connection_or_names_a_table_it_does_not_list()
    {
        var scope = await _app.OpenAsync(TestApp.Profile("Test"));
        var listed = _app.Shell.OpenTable(TestApp.Kunden)!;
        var foreign = AddTab(scope, new TableSummary("STAMMDATEN", "LAENDER", TableKind.Table));

        Assert.False(_app.Shell.RevealInExplorer(foreign));
        Assert.Null(_app.Shell.Reveal);
        Assert.Equal("LAENDER steht nicht im Explorer: eine Tabelle eines anderen Schemas, erreicht ohne Synonym.", _app.Shell.Notice?.Text);

        _app.Shell.ActivateTab(foreign);
        Assert.True(_app.Shell.RevealInExplorer(listed));
        Assert.Same(listed, _app.Shell.ActiveTab);
        Assert.Equal(new ExplorerReveal(1, scope.Id, TestApp.Kunden.Ref, null), _app.Shell.Reveal);
        Assert.True(_app.Shell.RevealInExplorer(listed));
        Assert.Equal(2, _app.Shell.Reveal!.Id); // the same again scrolls again
    }

    private TableTab AddTab(ConnectionScope scope, TableSummary table)
    {
        var workspace = _app.WorkspaceOf(scope);
        var tab = new TableTab(workspace.WorkspaceId, table);
        workspace.Tabs.Add(tab);
        return tab;
    }

    [Fact]
    public async Task Closing_a_tab_of_a_background_connection_closes_it()
    {
        var test = await _app.OpenAsync(TestApp.Profile("Test"));
        await _app.OpenAsync(TestApp.Profile("Dev"));
        var tab = _app.AddPendingWork(test);

        _app.Shell.CloseTab(tab);

        Assert.DoesNotContain(tab, _app.WorkspaceOf(test).Tabs);
    }

    /// <summary>WP-22: the DDL proposal of the schema comparison opens in the target connection once that one is shown.</summary>
    [Fact]
    public async Task A_script_for_another_connection_opens_there_once_it_is_shown()
    {
        var test = await _app.OpenAsync(TestApp.Profile("Test"));
        var dev = await _app.OpenAsync(TestApp.Profile("Dev")); // shown now

        _app.Shell.OpenSqlIn(test.Id, "ALTER TABLE kunden ADD (fax VARCHAR2(30));\n");
        Assert.Empty(_app.WorkspaceOf(test).Tabs.OfType<SqlTab>());
        Assert.Empty(_app.WorkspaceOf(dev).Tabs.OfType<SqlTab>());

        _app.Shell.ShowConnection(test.Id);

        var tab = Assert.Single(_app.WorkspaceOf(test).Tabs.OfType<SqlTab>());
        Assert.Equal("ALTER TABLE kunden ADD (fax VARCHAR2(30));\n", tab.Text);
        Assert.Same(tab, _app.Shell.ActiveTab);
        _app.Shell.ShowConnection(dev.Id);
        _app.Shell.ShowConnection(test.Id);
        Assert.Single(_app.WorkspaceOf(test).Tabs.OfType<SqlTab>()); // opened once
    }

    [Fact]
    public async Task New_sql_and_linq_tabs_take_the_first_free_number_after_the_active_tab()
    {
        await _app.OpenAsync(TestApp.Profile("Test"));

        var first = _app.Shell.OpenSql()!;
        var linq = _app.Shell.OpenLinq()!;
        _app.Shell.ActivateTab(first);
        var second = _app.Shell.OpenSql()!;

        Assert.Equal(["SQL 1", "LINQ 1", "SQL 2"], new[] { first.Title, linq.Title, second.Title });
        Assert.Equal([first, second, linq], _app.Shell.Tabs.ToList<WorkspaceTab>()); // right after the active one
        Assert.Same(second, _app.Shell.ActiveTab);
    }

    public ValueTask DisposeAsync() => _app.DisposeAsync();
}
