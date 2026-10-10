using FerretSharp.UI.State;

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
        var testTab = _app.AddPendingWork(test);
        var devTab = _app.AddPendingWork(dev);

        // Dev's refreshed schema no longer has KUNDEN.
        _app.Shell.RemoveTabsWhere(dev.Id, _ => true);

        Assert.DoesNotContain(devTab, _app.WorkspaceOf(dev).Tabs);
        Assert.Contains(testTab, _app.WorkspaceOf(test).Tabs);
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
