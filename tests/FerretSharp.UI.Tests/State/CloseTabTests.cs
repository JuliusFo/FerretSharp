using FerretSharp.UI.State;

namespace FerretSharp.UI.Tests.State;

/// <summary>Closing a tab asks first if something would be lost: pending changes, a typed value, editor text.</summary>
public sealed class CloseTabTests : IAsyncDisposable
{
    private readonly TestApp _app = new();

    [Fact]
    public async Task A_table_tab_without_pending_changes_closes_at_once()
    {
        var scope = await _app.OpenAsync(TestApp.Profile("Test"));
        var tab = _app.Shell.OpenTable(TestApp.Kunden)!;

        _app.Lifecycle.CloseTab(tab);

        Assert.Null(_app.Lifecycle.ConfirmCloseTab);
        Assert.DoesNotContain(tab, _app.WorkspaceOf(scope).Tabs);
    }

    [Fact]
    public async Task Pending_changes_ask_first()
    {
        var scope = await _app.OpenAsync(TestApp.Profile("Test"));
        var tab = _app.AddPendingWork(scope);

        _app.Lifecycle.CloseTab(tab, cellEditing: true);

        Assert.Equal(new CloseTabQuestion(tab, CloseTabLoss.PendingChanges), _app.Lifecycle.ConfirmCloseTab);
        Assert.Contains(tab, _app.WorkspaceOf(scope).Tabs);
    }

    [Fact]
    public async Task A_value_being_typed_in_the_grid_asks_first()
    {
        var scope = await _app.OpenAsync(TestApp.Profile("Test"));
        var tab = _app.Shell.OpenTable(TestApp.Kunden)!;

        _app.Lifecycle.CloseTab(tab, cellEditing: true);

        Assert.Equal(new CloseTabQuestion(tab, CloseTabLoss.TypedValue), _app.Lifecycle.ConfirmCloseTab);
        Assert.Contains(tab, _app.WorkspaceOf(scope).Tabs);
    }

    [Fact]
    public async Task A_value_typed_in_the_form_asks_first_only_for_its_own_tab()
    {
        var scope = await _app.OpenAsync(TestApp.Profile("Test"));
        var withForm = _app.Shell.OpenTable(TestApp.Kunden)!;
        var other = _app.Shell.OpenFiltered(TestApp.Kunden, [])!; // a second KUNDEN tab
        _app.Shell.TypedValuesOf += tab => tab == withForm; // what RowFormPanel answers with a draft

        _app.Lifecycle.CloseTab(other);
        _app.Lifecycle.CloseTab(withForm);

        Assert.Equal(new CloseTabQuestion(withForm, CloseTabLoss.TypedValue), _app.Lifecycle.ConfirmCloseTab);
        Assert.Equal([withForm], _app.WorkspaceOf(scope).Tabs.ToList<WorkspaceTab>());
    }

    [Fact]
    public async Task Sql_and_linq_tabs_with_text_ask_first_empty_ones_close()
    {
        var scope = await _app.OpenAsync(TestApp.Profile("Test"));
        var sql = _app.Shell.OpenSql()!;
        var linq = _app.Shell.OpenLinq()!;
        var empty = _app.Shell.OpenSql()!;
        empty.Text = " \n ";
        sql.Text = "SELECT * FROM KUNDEN";
        linq.Code = "Kunden.Take(10)";

        _app.Lifecycle.CloseTab(empty);
        Assert.Null(_app.Lifecycle.ConfirmCloseTab);

        _app.Lifecycle.CloseTab(linq);
        Assert.Equal(new CloseTabQuestion(linq, CloseTabLoss.EditorText), _app.Lifecycle.ConfirmCloseTab);
        _app.Lifecycle.CancelConfirmation();

        _app.Lifecycle.CloseTab(sql);
        Assert.Equal(new CloseTabQuestion(sql, CloseTabLoss.EditorText), _app.Lifecycle.ConfirmCloseTab);
        _app.Lifecycle.CloseTabConfirmed(sql);

        Assert.Null(_app.Lifecycle.ConfirmCloseTab);
        Assert.Equal([linq], _app.WorkspaceOf(scope).Tabs.ToList<WorkspaceTab>());
    }

    public ValueTask DisposeAsync() => _app.DisposeAsync();
}
