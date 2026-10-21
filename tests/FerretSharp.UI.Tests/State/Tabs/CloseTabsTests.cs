using FerretSharp.UI.State;

namespace FerretSharp.UI.Tests.State;

/// <summary>
/// Closing several tabs from a tab's right-click menu or a shortcut (3.27): which tabs, which stays active, and one question
/// for all tabs that would lose something – discard them, keep them open or cancel.
/// </summary>
public sealed class CloseTabsTests : IAsyncDisposable
{
    private readonly TestApp _app = new();

    /// <summary>Three empty SQL tabs in a row, the last one active.</summary>
    private async Task<(ConnectionScope Scope, SqlTab A, SqlTab B, SqlTab C)> ThreeTabsAsync()
    {
        var scope = await _app.OpenAsync(TestApp.Profile("Test"));
        var a = _app.Shell.OpenSql()!;
        var b = _app.Shell.OpenSql()!;
        var c = _app.Shell.OpenSql()!;
        return (scope, a, b, c);
    }

    [Fact]
    public async Task The_scopes_take_the_tabs_seen_from_the_target()
    {
        var (_, a, b, c) = await ThreeTabsAsync();
        IReadOnlyList<WorkspaceTab> tabs = [a, b, c];

        Assert.Equal([b], CloseTabsQuestion.Select(tabs, b, TabCloseScope.This));
        Assert.Equal([a, c], CloseTabsQuestion.Select(tabs, b, TabCloseScope.Others));
        Assert.Equal([c], CloseTabsQuestion.Select(tabs, b, TabCloseScope.ToTheRight));
        Assert.Empty(CloseTabsQuestion.Select(tabs, c, TabCloseScope.ToTheRight));
        Assert.Equal(tabs, CloseTabsQuestion.Select(tabs, b, TabCloseScope.All));
        Assert.Empty(CloseTabsQuestion.Select([a, c], b, TabCloseScope.All)); // the target is gone already
    }

    [Fact]
    public async Task Close_others_keeps_the_target_and_activates_it()
    {
        var (scope, a, _, _) = await ThreeTabsAsync();

        _app.Lifecycle.CloseTabs(a, TabCloseScope.Others);

        Assert.Null(_app.Lifecycle.ConfirmCloseTabs);
        Assert.Equal([a], _app.WorkspaceOf(scope).Tabs.ToList<WorkspaceTab>());
        Assert.Same(a, _app.Shell.ActiveTab);
    }

    [Fact]
    public async Task Close_to_the_right_activates_the_target_only_if_the_active_tab_closes()
    {
        var (scope, a, b, c) = await ThreeTabsAsync();

        _app.Lifecycle.CloseTabs(b, TabCloseScope.ToTheRight); // c was active
        Assert.Equal([a, b], _app.WorkspaceOf(scope).Tabs.ToList<WorkspaceTab>());
        Assert.Same(b, _app.Shell.ActiveTab);

        var d = _app.Shell.OpenSql()!; // [a, b, d]
        _app.Shell.ActivateTab(a);
        _app.Lifecycle.CloseTabs(b, TabCloseScope.ToTheRight);
        Assert.Equal([a, b], _app.WorkspaceOf(scope).Tabs.ToList<WorkspaceTab>());
        Assert.DoesNotContain(d, _app.WorkspaceOf(scope).Tabs);
        Assert.Same(a, _app.Shell.ActiveTab);
    }

    [Fact]
    public async Task Close_all_without_losses_closes_at_once()
    {
        var (scope, _, b, _) = await ThreeTabsAsync();

        _app.Lifecycle.CloseTabs(b, TabCloseScope.All);

        Assert.Null(_app.Lifecycle.ConfirmCloseTabs);
        Assert.Empty(_app.WorkspaceOf(scope).Tabs);
        Assert.Null(_app.Shell.ActiveTab);
    }

    [Fact]
    public async Task Tabs_that_would_lose_something_are_asked_about_once()
    {
        var (scope, a, b, c) = await ThreeTabsAsync();
        b.Text = "SELECT * FROM KUNDEN";
        var pending = _app.AddPendingWork(scope); // [a, b, c, pending]

        _app.Lifecycle.CloseTabs(c, TabCloseScope.All);

        var question = _app.Lifecycle.ConfirmCloseTabs!;
        Assert.Equal([new CloseTabQuestion(b, CloseTabLoss.EditorText), new CloseTabQuestion(pending, CloseTabLoss.PendingChanges)], question.Losing);
        Assert.Equal([a, c], question.Others);
        Assert.Equal(4, _app.WorkspaceOf(scope).Tabs.Count); // nothing closed before the answer
    }

    [Fact]
    public async Task Keeping_them_open_closes_only_the_others()
    {
        var (scope, a, b, c) = await ThreeTabsAsync();
        b.Text = "SELECT * FROM KUNDEN";
        var pending = _app.AddPendingWork(scope);

        _app.Lifecycle.CloseTabs(c, TabCloseScope.All);
        _app.Lifecycle.CloseTabsConfirmed(keepLosing: true);

        Assert.Null(_app.Lifecycle.ConfirmCloseTabs);
        Assert.Equal([b, pending], _app.WorkspaceOf(scope).Tabs.ToList<WorkspaceTab>());
        Assert.Same(pending, _app.Shell.ActiveTab); // c was active: the next one to its right
        Assert.DoesNotContain(a, _app.WorkspaceOf(scope).Tabs);
    }

    [Fact]
    public async Task Discarding_closes_all_and_cancel_closes_none()
    {
        var (scope, a, b, c) = await ThreeTabsAsync();
        b.Text = "SELECT * FROM KUNDEN";

        _app.Lifecycle.CloseTabs(a, TabCloseScope.Others);
        _app.Lifecycle.CancelConfirmation();
        Assert.Null(_app.Lifecycle.ConfirmCloseTabs);
        Assert.Equal([a, b, c], _app.WorkspaceOf(scope).Tabs.ToList<WorkspaceTab>());

        _app.Lifecycle.CloseTabs(a, TabCloseScope.Others);
        _app.Lifecycle.CloseTabsConfirmed(keepLosing: false);
        Assert.Equal([a], _app.WorkspaceOf(scope).Tabs.ToList<WorkspaceTab>());
        Assert.Same(a, _app.Shell.ActiveTab);
    }

    [Fact]
    public async Task One_tab_to_close_asks_like_closing_it()
    {
        var (scope, a, b, c) = await ThreeTabsAsync();
        c.Text = "SELECT 1 FROM DUAL";

        _app.Lifecycle.CloseTabs(b, TabCloseScope.ToTheRight);

        Assert.Null(_app.Lifecycle.ConfirmCloseTabs);
        Assert.Equal(new CloseTabQuestion(c, CloseTabLoss.EditorText), _app.Lifecycle.ConfirmCloseTab);
        Assert.Equal([a, b, c], _app.WorkspaceOf(scope).Tabs.ToList<WorkspaceTab>());
    }

    /// <summary>The shortcut says a grid cell is being edited: only the active tab's grid can be.</summary>
    [Fact]
    public async Task A_cell_being_edited_counts_for_the_active_tab_only()
    {
        var scope = await _app.OpenAsync(TestApp.Profile("Test"));
        var first = _app.Shell.OpenTable(TestApp.Kunden)!;
        var second = _app.Shell.OpenFiltered(TestApp.Kunden, [])!; // active

        _app.Lifecycle.CloseTabs(second, TabCloseScope.All, cellEditing: true);

        var question = _app.Lifecycle.ConfirmCloseTabs!;
        Assert.Equal([new CloseTabQuestion(second, CloseTabLoss.TypedValue)], question.Losing);
        _app.Lifecycle.CloseTabsConfirmed(keepLosing: true);
        Assert.Equal([second], _app.WorkspaceOf(scope).Tabs.ToList<WorkspaceTab>());
        Assert.DoesNotContain(first, _app.WorkspaceOf(scope).Tabs);
    }

    public ValueTask DisposeAsync() => _app.DisposeAsync();
}
