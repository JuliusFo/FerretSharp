using FerretSharp.UI.State;

namespace FerretSharp.UI.Tests.State;

/// <summary>Tabs dragged in the tab bar (small-tab-bar-rework): before or after another tab, only within their group.</summary>
public sealed class MoveTabTests : IAsyncDisposable
{
    private readonly TestApp _app = new();

    public ValueTask DisposeAsync() => _app.DisposeAsync();

    private async Task<(ConnectionScope Scope, SqlTab A, SqlTab B, SqlTab C, SqlTab D)> FourTabsAsync()
    {
        var scope = await _app.OpenAsync(TestApp.Profile("Test"));
        return (scope, _app.Shell.OpenSql()!, _app.Shell.OpenSql()!, _app.Shell.OpenSql()!, _app.Shell.OpenSql()!);
    }

    private List<WorkspaceTab> TabsOf(ConnectionScope scope) => [.. _app.WorkspaceOf(scope).Tabs];

    [Fact]
    public async Task A_tab_goes_before_or_after_the_target_and_the_active_tab_stays()
    {
        var (scope, a, b, c, d) = await FourTabsAsync();
        var changes = 0;
        _app.Shell.Changed += () => changes++;

        Assert.True(_app.Shell.MoveTab(a, c, after: true)); // to the right
        Assert.Equal([b, c, a, d], TabsOf(scope));

        Assert.True(_app.Shell.MoveTab(d, b, after: false)); // to the left
        Assert.Equal([d, b, c, a], TabsOf(scope));

        Assert.True(_app.Shell.MoveTab(d, a, after: true)); // to the end
        Assert.Equal([b, c, a, d], TabsOf(scope));

        Assert.Equal(3, changes); // the order is saved with the workspace
        Assert.Same(d, _app.Shell.ActiveTab);
    }

    [Fact]
    public async Task Dropped_on_its_own_place_nothing_changes()
    {
        var (scope, a, b, c, _) = await FourTabsAsync();
        var changes = 0;
        _app.Shell.Changed += () => changes++;

        Assert.False(_app.Shell.MoveTab(b, a, after: true));
        Assert.False(_app.Shell.MoveTab(b, c, after: false));
        Assert.False(_app.Shell.MoveTab(b, b, after: true));

        Assert.Equal([a, b, c], TabsOf(scope).Take(3));
        Assert.Equal(0, changes);
    }

    /// <summary>The pinned tabs stay first (WP-34): dragging does not pin or unpin.</summary>
    [Fact]
    public async Task A_tab_does_not_move_into_the_other_group_nor_into_another_workspace()
    {
        var (scope, a, b, c, d) = await FourTabsAsync();
        _app.Shell.SetPinned(a, true);
        _app.Shell.SetPinned(b, true); // [a, b | c, d]

        Assert.False(_app.Shell.MoveTab(c, a, after: false));
        Assert.False(_app.Shell.MoveTab(a, d, after: true));
        Assert.True(_app.Shell.MoveTab(a, b, after: true));
        Assert.Equal([b, a, c, d], TabsOf(scope));

        var other = await _app.OpenAsync(TestApp.Profile("Other"));
        var e = _app.Shell.OpenSql()!;
        Assert.False(_app.Shell.MoveTab(e, c, after: false));
        Assert.Equal([b, a, c, d], TabsOf(scope));
        Assert.Equal([e], TabsOf(other));
    }
}
