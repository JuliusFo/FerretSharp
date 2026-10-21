using FerretSharp.Core.Workspaces;
using FerretSharp.UI.State;

namespace FerretSharp.UI.Tests.State;

/// <summary>
/// Pinned tabs (WP-34): they stand first, new tabs never land among them, closing other tabs, the tabs to the right or all
/// tabs leaves them open, and the pin is saved with the workspace.
/// </summary>
public sealed class PinnedTabsTests : IAsyncDisposable
{
    private readonly TestApp _app = new();

    public ValueTask DisposeAsync() => _app.DisposeAsync();

    private async Task<(ConnectionScope Scope, SqlTab A, SqlTab B, SqlTab C)> ThreeTabsAsync()
    {
        var scope = await _app.OpenAsync(TestApp.Profile("Test"));
        return (scope, _app.Shell.OpenSql()!, _app.Shell.OpenSql()!, _app.Shell.OpenSql()!);
    }

    private List<WorkspaceTab> TabsOf(ConnectionScope scope) => [.. _app.WorkspaceOf(scope).Tabs];

    [Fact]
    public async Task A_pinned_tab_goes_to_the_end_of_the_pinned_ones_and_back_right_after_them()
    {
        var (scope, a, b, c) = await ThreeTabsAsync();

        _app.Shell.SetPinned(c, true);
        Assert.Equal([c, a, b], TabsOf(scope));

        _app.Shell.SetPinned(b, true);
        Assert.Equal([c, b, a], TabsOf(scope));
        Assert.Equal(2, _app.WorkspaceOf(scope).PinnedCount);

        _app.Shell.SetPinned(c, false);
        Assert.Equal([b, c, a], TabsOf(scope));
        Assert.Equal((false, true), (c.Pinned, b.Pinned));
        Assert.Same(c, _app.Shell.ActiveTab); // pinning does not change the active tab
    }

    [Fact]
    public async Task New_tabs_open_after_the_pinned_ones_even_from_a_pinned_active_tab()
    {
        var (scope, a, b, _) = await ThreeTabsAsync();
        _app.Shell.SetPinned(a, true);
        _app.Shell.SetPinned(b, true);
        _app.Shell.ActivateTab(a);

        var d = _app.Shell.OpenSql()!;

        Assert.Equal(d, TabsOf(scope)[2]);
        Assert.False(d.Pinned);
    }

    [Fact]
    public async Task Closing_others_to_the_right_or_all_leaves_pinned_tabs_open()
    {
        var (scope, a, b, c) = await ThreeTabsAsync();
        _app.Shell.SetPinned(b, true); // [b, a, c]
        IReadOnlyList<WorkspaceTab> tabs = TabsOf(scope);

        Assert.Equal([c], CloseTabsQuestion.Select(tabs, a, TabCloseScope.Others));
        Assert.Equal([a, c], CloseTabsQuestion.Select(tabs, b, TabCloseScope.ToTheRight));
        Assert.Equal([a, c], CloseTabsQuestion.Select(tabs, b, TabCloseScope.All));
        Assert.Equal([b], CloseTabsQuestion.Select(tabs, b, TabCloseScope.This)); // closing the tab itself still works

        _app.Lifecycle.CloseTabs(a, TabCloseScope.All);

        Assert.Equal([b], TabsOf(scope));
        Assert.Same(b, _app.Shell.ActiveTab);
    }

    [Fact]
    public async Task The_pin_is_saved_and_restored_with_pinned_tabs_first()
    {
        var (scope, a, b, _) = await ThreeTabsAsync();
        _app.Shell.SetPinned(b, true);

        var states = TabsOf(scope).Select(t => t.Save(TabsOf(scope))).ToList();
        Assert.Equal([true, false, false], states.Select(s => s.Pinned));

        var saved = new Workspace(Guid.NewGuid(), scope.Id, "Gespeichert")
        {
            // A pinned tab behind an unpinned one: restored to the front.
            Tabs = [TabState.OfSql(new SqlTabState("Frei", "", [])), TabState.OfSql(new SqlTabState("Fest", "", [])) with { Pinned = true }],
            ActiveTabIndex = 0,
        };
        _app.Shell.SyncWorkspaces(scope.Id, [saved], saved.Id, scope.Active.Schema!);

        var restored = _app.Shell.AllWorkspaces.Single(w => w.WorkspaceId == saved.Id);
        Assert.Equal(["Fest", "Frei"], restored.Tabs.Cast<SqlTab>().Select(t => t.Title));
        Assert.Equal([true, false], restored.Tabs.Select(t => t.Pinned));
        Assert.Equal("Frei", ((SqlTab)restored.ActiveTab!).Title); // the active tab follows its tab, not its index
    }
}
