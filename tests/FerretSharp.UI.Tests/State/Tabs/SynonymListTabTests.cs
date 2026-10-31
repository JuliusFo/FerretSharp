using FerretSharp.Core.Schema;
using FerretSharp.Core.Settings;
using FerretSharp.Core.Workspaces;
using FerretSharp.UI.State;

namespace FerretSharp.UI.Tests.State;

/// <summary>
/// WP-68: the tab of the schema's synonyms (one per workspace, saved with its filter, reloads with F5) and "Synonym löschen"
/// in the object menu, which opens the object's tab on its "Synonyms" view with the drop asked for.
/// </summary>
public sealed class SynonymListTabTests : IAsyncDisposable
{
    private readonly TestApp _app = new();

    [Fact]
    public async Task The_palette_opens_one_tab_per_workspace_saved_with_its_filter()
    {
        var scope = await _app.OpenAsync(TestApp.Profile("Test"));
        var list = _app.Shell.OpenSynonymList()!;
        _app.Shell.OpenSql();

        Assert.Same(list, _app.Shell.OpenSynonymList());
        Assert.Same(list, _app.Shell.ActiveTab);
        Assert.Equal("Synonyme", list.Caption);

        list.OnlyBroken = true;
        var saved = new Workspace(Guid.NewGuid(), scope.Id, "Gespeichert") { Tabs = [list.ToState(_app.Shell.Tabs)], ActiveTabIndex = 0 };
        _app.Shell.SyncWorkspaces(scope.Id, [saved], saved.Id, scope.Active.Schema!);

        var restored = Assert.IsType<SynonymListTab>(Assert.Single(_app.Shell.AllWorkspaces.Single(w => w.WorkspaceId == saved.Id).Tabs));
        Assert.True(restored.OnlyBroken);
    }

    [Fact]
    public async Task Dropping_a_synonym_from_the_menu_opens_the_object_on_its_synonyms()
    {
        await _app.OpenAsync(TestApp.Profile("Test"));
        var synonym = new SynonymInfo("PUBLIC", "KUNDEN_ALT");

        _app.Shell.RequestSynonymDrop(TestApp.Kunden with { Synonym = synonym }, synonym);

        var tab = Assert.IsType<TableTab>(_app.Shell.ActiveTab);
        Assert.Equal((TabMode.Synonyms, synonym), (tab.Mode, tab.DropRequested));
    }

    [Fact]
    public void The_list_reloads_with_f5_and_a_trigger_has_no_synonyms()
    {
        var ready = new ShellActionContext(
            InExplorer: true, Connected: true, ActiveTab: new SynonymListTab(Guid.NewGuid()), Writable: true, LinkedProject: false, CanGoBack: false, CanGoForward: false,
            PreviousConnection: false);

        Assert.Null(ShellActions.Unavailable(ShortcutAction.Refresh, ready));
        Assert.Equal("Nicht in diesem Tab.", ShellActions.Unavailable(ShortcutAction.FindColumn, ready));
        Assert.Null(ShellActions.Unavailable(ShortcutAction.SchemaSynonyms, ready with { ActiveTab = null }));
        Assert.DoesNotContain(PlSqlView.Synonyms, PlSqlTab.ViewsOf(PlSqlKind.Trigger));
        Assert.Contains(PlSqlView.Synonyms, PlSqlTab.ViewsOf(PlSqlKind.Package));
    }

    public ValueTask DisposeAsync() => _app.DisposeAsync();
}
