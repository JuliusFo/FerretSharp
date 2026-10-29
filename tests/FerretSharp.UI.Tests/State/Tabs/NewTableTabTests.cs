using FerretSharp.Core.Design;
using FerretSharp.Core.Schema;
using FerretSharp.UI.State;

namespace FerretSharp.UI.Tests.State;

/// <summary>
/// A new table in the designer (WP-23, stage 3): a tab of its own that is not saved, asks before its draft is lost, and
/// whose draft goes on as an edit of the table once CREATE TABLE ran.
/// </summary>
public sealed class NewTableTabTests : IAsyncDisposable
{
    private readonly TestApp _app = new();

    [Fact]
    public async Task A_new_table_tab_starts_with_a_column_and_is_not_saved_with_the_workspace()
    {
        var scope = await _app.OpenAsync(TestApp.Profile("Test"));
        var table = _app.Shell.OpenTable(TestApp.Kunden)!;
        var draft = _app.Shell.OpenNewTable("APP_USER")!;

        Assert.Single(draft.Designer.Columns);
        Assert.Same(draft, _app.Shell.ActiveTab);
        _app.Lifecycle.SaveTabs();

        var saved = scope.Workspaces.Find(scope.Workspaces.Active!.Id)!;
        Assert.Single(saved.Tabs); // KUNDEN only
        Assert.Equal(-1, saved.ActiveTabIndex); // the active tab is the draft, which is not saved

        _app.Shell.ActivateTab(table);
        _app.Lifecycle.SaveTabs();
        Assert.Equal(0, scope.Workspaces.Find(scope.Workspaces.Active!.Id)!.ActiveTabIndex);
    }

    [Fact]
    public async Task Closing_a_draft_asks_first_an_untouched_one_closes()
    {
        await _app.OpenAsync(TestApp.Profile("Test"));
        var draft = _app.Shell.OpenNewTable("APP_USER")!;
        draft.Designer.Columns.Clear();

        _app.Lifecycle.CloseTab(draft);
        Assert.Null(_app.Lifecycle.ConfirmCloseTab);

        var second = _app.Shell.OpenNewTable("APP_USER")!;
        second.Designer.Columns[0].Name = "ID";
        _app.Lifecycle.CloseTab(second);
        Assert.Equal(new CloseTabQuestion(second, CloseTabLoss.TableDraft), _app.Lifecycle.ConfirmCloseTab);
    }

    [Fact]
    public async Task A_table_tab_in_edit_mode_with_changes_asks_before_closing()
    {
        await _app.OpenAsync(TestApp.Profile("Test"));
        var tab = _app.Shell.OpenTable(TestApp.Kunden)!;
        var designer = new TableDesignerDraft(_app.KundenDetails, []);
        tab.Designer = new DesignerSession(designer);
        _app.Lifecycle.CloseTab(tab);
        Assert.Null(_app.Lifecycle.ConfirmCloseTab); // nothing changed yet

        tab = _app.Shell.OpenTable(TestApp.Kunden)!;
        designer.AddColumn().Name = "FAX";
        tab.Designer = new DesignerSession(designer);
        _app.Lifecycle.CloseTab(tab);
        Assert.Equal(CloseTabLoss.TableDraft, _app.Lifecycle.ConfirmCloseTab?.Loss);
    }

    [Fact]
    public async Task The_name_and_the_suggested_constraint_names_follow_what_is_typed()
    {
        await _app.OpenAsync(TestApp.Profile("Test"));
        var draft = _app.Shell.OpenNewTable("APP_USER")!;
        draft.Designer.Columns[0].Name = "ID";
        var key = draft.Designer.AddConstraint(ConstraintType.PrimaryKey);
        key.Columns.Add("ID");

        draft.Name = "lieferant";
        draft.Designer.TableName = draft.Table.Name;
        draft.Designer.SuggestName(key);

        Assert.Equal(new NewTable("APP_USER", "LIEFERANT"), draft.Table);
        Assert.Equal("PK_LIEFERANT", key.Name);
    }

    /// <summary>
    /// CREATE TABLE ran, a constraint after it failed: the draft laid over the created table holds only the constraint – it
    /// goes on in the table's tab.
    /// </summary>
    [Fact]
    public async Task After_create_table_the_rest_of_the_draft_is_an_edit_of_the_table()
    {
        await _app.OpenAsync(TestApp.Profile("Test"));
        var draft = _app.Shell.OpenNewTable("APP_USER")!;
        draft.Name = "ORT";
        var id = draft.Designer.Columns[0];
        (id.Name, id.Type, id.Size, id.Scale, id.NotNull) = ("ID", "NUMBER", "10", "0", true);
        var plz = draft.Designer.AddColumn();
        (plz.Name, plz.Size) = ("PLZ", "5");
        var check = draft.Designer.AddConstraint(ConstraintType.Check);
        check.Condition = "PLZ IS NOT NULL";

        var created = new TableDetails(new TableSummary("APP_USER", "ORT", TableKind.Table),
            [draft.Designer.Columns[0].ToColumn().Column! with { Position = 1 }, draft.Designer.Columns[1].ToColumn().Column! with { Position = 2 }], [], [], false);
        var rest = draft.Designer.Rebase(created, []);

        Assert.All(rest.Columns, c => Assert.False(c.IsNew));
        Assert.Equal(
            [$"""ALTER TABLE "APP_USER"."ORT" ADD CONSTRAINT "{check.Name}" CHECK (PLZ IS NOT NULL)"""],
            TableDesign.Plan(DesignContext.Of(created), rest.ToDraft().Draft, DataFacts.None).Statements);
    }

    public ValueTask DisposeAsync() => _app.DisposeAsync();
}
