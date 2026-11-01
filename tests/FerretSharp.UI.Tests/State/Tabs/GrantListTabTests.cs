using FerretSharp.Core.Schema;
using FerretSharp.Core.Settings;
using FerretSharp.Core.Workspaces;
using FerretSharp.UI.State;

namespace FerretSharp.UI.Tests.State;

/// <summary>
/// WP-71: the tab of the schema's privileges (one per workspace, saved with its grantee filter, reloads with F5) and the
/// view "Privileges" of PL/SQL units (not triggers).
/// </summary>
public sealed class GrantListTabTests : IAsyncDisposable
{
    private readonly TestApp _app = new();

    [Fact]
    public async Task The_palette_opens_one_tab_per_workspace_saved_with_its_filter()
    {
        var scope = await _app.OpenAsync(TestApp.Profile("Test"));
        var list = _app.Shell.OpenGrantList()!;
        _app.Shell.OpenSql();

        Assert.Same(list, _app.Shell.OpenGrantList());
        Assert.Same(list, _app.Shell.ActiveTab);
        Assert.Equal("Berechtigungen", list.Caption);

        (list.Grantee, list.OnlyWithout) = ("APP_USER", true);
        var saved = new Workspace(Guid.NewGuid(), scope.Id, "Gespeichert") { Tabs = [list.ToState(_app.Shell.Tabs)], ActiveTabIndex = 0 };
        _app.Shell.SyncWorkspaces(scope.Id, [saved], saved.Id, scope.Active.Schema!);

        var restored = Assert.IsType<GrantListTab>(Assert.Single(_app.Shell.AllWorkspaces.Single(w => w.WorkspaceId == saved.Id).Tabs));
        Assert.Equal(("APP_USER", true), (restored.Grantee, restored.OnlyWithout));
    }

    [Fact]
    public void The_list_reloads_with_f5_and_a_trigger_has_no_privileges()
    {
        var ready = new ShellActionContext(
            InExplorer: true, Connected: true, ActiveTab: new GrantListTab(Guid.NewGuid()), Writable: true, LinkedProject: false, CanGoBack: false, CanGoForward: false,
            PreviousConnection: false);

        Assert.Null(ShellActions.Unavailable(ShortcutAction.Refresh, ready));
        Assert.Null(ShellActions.Unavailable(ShortcutAction.SchemaGrants, ready with { ActiveTab = null }));
        Assert.DoesNotContain(PlSqlView.Grants, PlSqlTab.ViewsOf(PlSqlKind.Trigger));
        Assert.Contains(PlSqlView.Grants, PlSqlTab.ViewsOf(PlSqlKind.Function));
    }

    public ValueTask DisposeAsync() => _app.DisposeAsync();
}
