using FerretSharp.Core.Schema;
using FerretSharp.Core.Workspaces;
using FerretSharp.UI.State;

namespace FerretSharp.UI.Tests.State;

/// <summary>PL/SQL tabs (WP-28): opening, jumping to a line, saving and restoring, dropping vanished units.</summary>
public sealed class PlSqlTabTests : IAsyncDisposable
{
    private readonly TestApp _app = new();

    [Fact]
    public async Task Opening_a_unit_twice_activates_its_tab()
    {
        await _app.OpenAsync(TestApp.Profile("Test"));
        var sql = _app.Shell.OpenSql()!;

        var tab = _app.Shell.OpenPlSql(TestApp.Rechnung)!;
        _app.Shell.ActivateTab(sql);
        var again = _app.Shell.OpenPlSql(TestApp.Rechnung, PlSqlView.Parameters);

        Assert.Same(tab, again);
        Assert.Same(tab, _app.Shell.ActiveTab);
        Assert.Equal(PlSqlView.Parameters, tab.View);
        Assert.Single(_app.Shell.Tabs.OfType<PlSqlTab>());
    }

    [Fact]
    public async Task Opening_at_a_line_shows_the_part_and_leaves_the_position_for_the_source_view()
    {
        await _app.OpenAsync(TestApp.Profile("Test"));

        var tab = _app.Shell.OpenPlSql(TestApp.Rechnung, at: new SourcePosition(PlSqlPart.Body, 120, 7))!;

        Assert.Equal(PlSqlView.Body, tab.View);
        Assert.Equal(new SourcePosition(PlSqlPart.Body, 120, 7), tab.Reveal);
    }

    [Fact]
    public void A_view_the_kind_does_not_have_is_not_chosen()
    {
        var trigger = new PlSqlObjectSummary("APP", "TRG", PlSqlKind.Trigger, "VALID");
        var procedure = new PlSqlObjectSummary("APP", "P", PlSqlKind.Procedure, "VALID");

        Assert.Equal([PlSqlView.Source, PlSqlView.Errors, PlSqlView.Dependencies], new PlSqlTab(Guid.NewGuid(), trigger).Views);
        Assert.DoesNotContain(PlSqlView.Body, new PlSqlTab(Guid.NewGuid(), procedure).Views);
        Assert.Equal(PlSqlView.Source, PlSqlTab.Restore(Guid.NewGuid(), trigger, new PlSqlTabState("APP", "TRG", PlSqlKind.Trigger, PlSqlView.Parameters)).View);
    }

    [Fact]
    public async Task Tabs_are_saved_and_restored_while_the_unit_exists()
    {
        var scope = await _app.OpenAsync(TestApp.Profile("Test"));
        var tab = _app.Shell.OpenPlSql(TestApp.Rechnung, PlSqlView.Body)!;
        var state = tab.ToState(_app.Shell.Tabs);
        Assert.Equal(new PlSqlTabState("APP_USER", "PKG_RECHNUNG", PlSqlKind.Package, PlSqlView.Body), state.PlSql);

        var saved = new Workspace(Guid.NewGuid(), scope.Id, "Gespeichert")
        {
            Tabs = [state, TabState.OfPlSql(new PlSqlTabState("APP_USER", "PKG_WEG", PlSqlKind.Package, PlSqlView.Source))],
            ActiveTabIndex = 0,
        };
        _app.Shell.SyncWorkspaces(scope.Id, [saved], saved.Id, scope.Active.Schema!);

        var restored = Assert.IsType<PlSqlTab>(Assert.Single(_app.Shell.AllWorkspaces.Single(w => w.WorkspaceId == saved.Id).Tabs));
        Assert.Equal((TestApp.Rechnung.Ref, PlSqlView.Body), (restored.Unit.Ref, restored.View));
    }

    /// <summary>
    /// Review of WP-68: a unit of another schema stays like a table there – dropping its last synonym closed the tab that
    /// had just dropped it.
    /// </summary>
    [Fact]
    public async Task A_schema_refresh_drops_tabs_of_units_that_are_gone_from_the_browsed_schema()
    {
        var scope = await _app.OpenAsync(TestApp.Profile("Test"));
        var tab = _app.Shell.OpenPlSql(TestApp.Rechnung)!;
        var foreign = _app.Shell.OpenPlSql(new PlSqlObjectSummary("STAMMDATEN", "PKG_LAENDER", PlSqlKind.Package, "VALID"))!;
        var table = _app.Shell.OpenTable(TestApp.Kunden)!;

        _app.PlSqlObjects = [];
        await scope.Active.Schema!.RefreshAsync(TestContext.Current.CancellationToken);
        _app.Shell.RemoveVanishedTabs(scope.Id, scope.Active.Schema!);

        Assert.DoesNotContain(tab, _app.Shell.Tabs);
        Assert.Contains(foreign, _app.Shell.Tabs);
        Assert.Contains(table, _app.Shell.Tabs);
    }

    public ValueTask DisposeAsync() => _app.DisposeAsync();
}
