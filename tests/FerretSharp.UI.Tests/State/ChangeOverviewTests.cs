using FerretSharp.Core.ClrModel;
using FerretSharp.Core.Connections;
using FerretSharp.Core.Data;
using FerretSharp.Core.Query;
using FerretSharp.Core.Schema;
using FerretSharp.UI.State;

namespace FerretSharp.UI.Tests.State;

/// <summary>WP-30: the change overview's content and undo up to an action / redo through <see cref="WorkspaceEditing"/>.</summary>
public sealed class ChangeOverviewTests : IAsyncDisposable
{
    private readonly TestApp _app = new();

    private static readonly TableDetails Kunden = new(
        TestApp.Kunden,
        [
            new ColumnInfo("ID", "NUMBER", null, false, 10, 0, false, false, null, 1),
            new ColumnInfo("NAME", "VARCHAR2", 20, true, null, null, true, false, null, 2),
        ],
        ["ID"], [], false);

    private static RowData Row(decimal id, string name) => new(new RowKey.PrimaryKey([id]), [id, name]);

    private (WorkspaceTabs Workspace, TableTab Tab) AddTab(ConnectionScope scope)
    {
        var workspace = _app.WorkspaceOf(scope);
        var tab = new TableTab(workspace.WorkspaceId, TestApp.Kunden) { Changes = new ChangeTracker(Kunden) };
        workspace.Tabs.Add(tab);
        return (workspace, tab);
    }

    private static async Task<FakeDataEditor> EditorAsync(ConnectionScope scope) =>
        (FakeDataEditor)await scope.Workspaces.GetEditorAsync(scope.Workspaces.Active!.Id, CancellationToken.None);

    [Fact]
    public async Task Pending_rows_by_tab_with_key_and_old_and_new_values()
    {
        var (workspace, tab) = AddTab(await _app.OpenAsync(TestApp.Profile("Test")));
        tab.Changes!.SetValue(Row(4711, "Meier"), 1, "Müller");
        tab.Changes.Delete(Row(12, "Weg"));
        tab.Changes.SetValue(tab.Changes.AddRow(), 1, "Neu");

        var group = Assert.Single(ChangeOverview.Pending(workspace, TablePresentation.Plain));

        Assert.Same(tab, group.Tab);
        Assert.Equal([OperationKind.Insert, OperationKind.Update, OperationKind.Delete], group.Rows.Select(r => r.Kind));
        var (added, changed, deleted) = (group.Rows[0], group.Rows[1], group.Rows[2]);
        Assert.Null(added.Key);
        Assert.Equal(new PendingCell(1, "NAME", null, "Neu"), Assert.Single(added.Cells));
        Assert.Equal("4.711", changed.Key); // as the grid and the form show it
        Assert.Equal(new PendingCell(1, "NAME", "Meier", "Müller"), Assert.Single(changed.Cells));
        Assert.Equal("12", deleted.Key);
        Assert.Empty(deleted.Cells);
    }

    [Fact]
    public void The_sql_of_an_action_shows_bind_values_but_not_the_rowid_output()
    {
        var action = new WriteAction(Guid.NewGuid(), WriteActionKind.Grid, "KUNDEN: 1 neu", 1, DateTimeOffset.Now)
        {
            Statements =
            [
                new QuerySpec("INSERT INTO KUNDEN (NAME) VALUES (:v1) RETURNING ROWID INTO :p_rowid",
                    [new QueryParameter("v1", "Müller", OracleTypeHint.Varchar2), new QueryParameter("p_rowid", null, OracleTypeHint.RowId, Output: true)]),
            ],
        };

        var sql = ChangeOverview.Sql(action);

        Assert.Contains("-- :v1 = 'Müller'", sql);
        Assert.DoesNotContain("p_rowid =", sql);
        Assert.EndsWith(";", sql);
    }

    [Fact]
    public async Task Undo_up_to_a_grid_write_takes_the_later_statement_along_and_redo_writes_the_same_rows_again()
    {
        var scope = await _app.OpenAsync(TestApp.Profile("Test"));
        var (workspace, tab) = AddTab(scope);
        tab.Changes!.SetValue(Row(1, "Meier"), 1, "Müller");
        tab.Changes.SetValue(Row(2, "Lang"), 1, "Kurz");
        Assert.True(await _app.Editing.FlushAsync(workspace));
        var editor = await EditorAsync(scope);
        var sqlTab = new SqlTab(workspace.WorkspaceId, "SQL 1");
        workspace.Tabs.Add(sqlTab);
        var statement = await editor.ExecuteAsync(new QuerySpec("UPDATE KUNDEN SET NAME = 'x'", []), CancellationToken.None);
        _app.Editing.NoteStatement(sqlTab, statement);
        var grid = editor.Actions[0];
        Assert.Same(tab, _app.Editing.SourceOf(workspace, grid));
        Assert.Same(sqlTab, _app.Editing.SourceOf(workspace, statement));

        Assert.True(await _app.Editing.UndoToAsync(workspace, grid.Id, statement.Id));

        Assert.Empty(editor.Actions);
        Assert.Equal([grid.Id, statement.Id], _app.Editing.SummaryOf(workspace).Undone.Select(a => a.Id));
        Assert.Equal(2, tab.Changes.PendingCount); // the grid write is pending again
        Assert.Null(_app.Editing.RedoBlocker(workspace));

        tab.Changes.SetValue(Row(3, "Neu"), 1, "Anders"); // another row of the tab: not part of the write
        Assert.True(await _app.Editing.RedoGridAsync(workspace, grid.Id));

        Assert.Equal(grid.Id, editor.LastFlush!.RedoOf);
        var again = Assert.Single(editor.Actions);
        Assert.Equal(grid.Id, again.RedoOf);
        Assert.Equal(2, again.Rows);
        Assert.Equal(1, tab.Changes.PendingCount); // only the other row is still pending
        Assert.Equal([statement.Id], _app.Editing.SummaryOf(workspace).Undone.Select(a => a.Id));
        Assert.Same(tab, _app.Editing.SourceOf(workspace, again));
    }

    [Fact]
    public async Task Redo_of_a_grid_write_is_blocked_while_its_rows_were_edited_since()
    {
        var scope = await _app.OpenAsync(TestApp.Profile("Test"));
        var (workspace, tab) = AddTab(scope);
        tab.Changes!.SetValue(Row(1, "Meier"), 1, "Müller");
        await _app.Editing.FlushAsync(workspace);
        Assert.True(await _app.Editing.UndoLastAsync(workspace));

        tab.Changes.SetValue(Row(1, "Meier"), 1, "Schulz");
        Assert.NotNull(_app.Editing.RedoBlocker(workspace));
        Assert.False(await _app.Editing.RedoGridAsync(workspace, _app.Editing.SummaryOf(workspace).Undone[0].Id));

        tab.Changes.UndoEdit(); // Ctrl+Z in the grid
        Assert.Null(_app.Editing.RedoBlocker(workspace));

        workspace.Tabs.Remove(tab);
        Assert.NotNull(_app.Editing.RedoBlocker(workspace));
    }

    [Fact]
    public async Task Redo_of_a_statement_asks_first_and_a_different_row_count_can_be_undone_again()
    {
        var scope = await _app.OpenAsync(TestApp.Profile("Test"));
        var workspace = _app.WorkspaceOf(scope);
        var editor = await EditorAsync(scope);
        var statement = await editor.ExecuteAsync(new QuerySpec("UPDATE KUNDEN SET NAME = 'x'", []), CancellationToken.None);
        await _app.Editing.UndoLastAsync(workspace);

        await _app.Lifecycle.RequestRedo(workspace, _app.Editing.SummaryOf(workspace).Undone[0]);
        Assert.Equal(statement.Id, _app.Lifecycle.ConfirmRedo?.Action.Id);
        Assert.Empty(editor.Actions); // nothing runs before the user confirmed

        editor.StatementRows = 3; // the data changed meanwhile
        await _app.Lifecycle.RedoConfirmedAsync();

        Assert.Null(_app.Lifecycle.ConfirmRedo);
        Assert.Equal((1, 3), (_app.Lifecycle.RedoRowsDiffer?.First.Rows, _app.Lifecycle.RedoRowsDiffer?.Again.Rows));
        Assert.Equal(statement.Id, Assert.Single(editor.Actions).RedoOf);
        Assert.Equal(WriteFate.Open, WorkspaceEditing.FateOf(scope.Workspaces, workspace.WorkspaceId, statement.Id, editor.Transaction.StartedAt));

        await _app.Lifecycle.ResolveRedoRowsAsync(keep: false);

        Assert.Null(_app.Lifecycle.RedoRowsDiffer);
        Assert.Empty(editor.Actions);
        Assert.Single(editor.Undone); // undone again – and still redoable
    }

    [Fact]
    public async Task A_new_write_ends_redo()
    {
        var scope = await _app.OpenAsync(TestApp.Profile("Test"));
        var workspace = _app.WorkspaceOf(scope);
        var editor = await EditorAsync(scope);
        await editor.ExecuteAsync(new QuerySpec("UPDATE KUNDEN SET NAME = 'x'", []), CancellationToken.None);
        await _app.Editing.UndoLastAsync(workspace);
        Assert.True(_app.Editing.SummaryOf(workspace).CanRedo);

        await editor.ExecuteAsync(new QuerySpec("DELETE FROM KUNDEN", []), CancellationToken.None);

        Assert.False(_app.Editing.SummaryOf(workspace).CanRedo);
    }

    public ValueTask DisposeAsync() => _app.DisposeAsync();
}
