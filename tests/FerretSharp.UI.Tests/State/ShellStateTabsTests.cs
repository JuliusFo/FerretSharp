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

    public ValueTask DisposeAsync() => _app.DisposeAsync();
}
