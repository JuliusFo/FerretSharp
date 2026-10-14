using FerretSharp.Core.Connections;
using FerretSharp.Core.Query;
using FerretSharp.UI.State;

namespace FerretSharp.UI.Tests.State;

/// <summary>
/// R5b (review 3.31.0, B15): a locked workspace – Prod as it opens – writes nothing on any path: SQL editor, LINQ console,
/// DDL of the editor and of the designer, the grid. Before, the test app opened Prod writable and the fake never refused.
/// </summary>
public sealed class LockedWorkspaceTests : IAsyncDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Update = "UPDATE kunden SET name = 'x'";
    private const string AddColumn = "ALTER TABLE kunden ADD (notiz VARCHAR2(100))";

    private readonly TestApp _app = new();

    private StatementRunner Runner => _app.Lifecycle.Statements;

    private async Task<(ConnectionScope Scope, WorkspaceTabs Workspace)> OpenProdAsync()
    {
        var scope = await _app.OpenAsync(TestApp.Profile("Prod", ConnectionKind.Prod));
        Assert.False(scope.Workspaces.IsWritable(scope.Workspaces.Active!.Id));
        return (scope, _app.WorkspaceOf(scope));
    }

    private void AssertNothingWritten()
    {
        Assert.All(_app.Editors, editor => Assert.Empty(editor.Actions));
        Assert.Empty(_app.RanDdl);
    }

    [Fact]
    public async Task A_statement_of_the_sql_editor_is_refused_and_stays_out_of_the_history()
    {
        var (scope, workspace) = await OpenProdAsync();
        var sql = new SqlTab(workspace.WorkspaceId, "SQL 1");
        workspace.Tabs.Add(sql);

        var outcome = await Runner.WriteAsync(sql, new QuerySpec(Update, []), StatementLog.Of(Update), Ct);

        Assert.Equal((true, null), (outcome.Refused, outcome.Written));
        AssertNothingWritten();
        Assert.Empty(await _app.History.LoadAsync(scope.Profile!.Id, Ct)); // it never ran
    }

    [Fact]
    public async Task A_write_of_the_linq_console_is_refused()
    {
        var (_, workspace) = await OpenProdAsync();
        var linq = new LinqTab(workspace.WorkspaceId, "LINQ 1");
        workspace.Tabs.Add(linq);

        var outcome = await Runner.WriteAsync(linq, new QuerySpec("DELETE FROM kunden WHERE id = :p0", [new QueryParameter("p0", 1m)]), null, Ct);

        Assert.Equal((true, null), (outcome.Refused, outcome.Written));
        AssertNothingWritten();
    }

    [Fact]
    public async Task Ddl_of_the_sql_editor_is_refused_and_changes_no_schema()
    {
        var (scope, _) = await OpenProdAsync();

        var outcome = await Runner.DdlAsync(scope.Workspaces.Active!.Id, AddColumn, StatementLog.Of(AddColumn), Ct);

        Assert.Equal((true, false), (outcome.Refused, outcome.SchemaChanged));
        AssertNothingWritten();
    }

    [Fact]
    public async Task Ddl_of_the_designer_stops_at_the_first_statement()
    {
        var (scope, _) = await OpenProdAsync();
        DdlRunResult? ended = null;

        var result = await Runner.RunDdlAsync(scope.Workspaces.Active!.Id, [AddColumn, "COMMENT ON TABLE kunden IS 'x'"], null, null, r => ended = r, Ct);

        Assert.Same(result, ended);
        Assert.Equal((AddColumn, 0), (result.Failed, result.Ran.Count));
        Assert.NotNull(result.Error);
        AssertNothingWritten();
    }

    [Fact]
    public async Task Pending_grid_changes_are_not_written_and_stay_pending()
    {
        var (scope, workspace) = await OpenProdAsync();
        var tab = _app.AddPendingWork(scope);

        Assert.False(await _app.Editing.FlushAsync(workspace));

        AssertNothingWritten();
        Assert.Equal(1, tab.Changes!.PendingCount);
    }

    [Fact]
    public async Task Unlocked_the_workspace_writes()
    {
        var (scope, workspace) = await OpenProdAsync();
        var sql = new SqlTab(workspace.WorkspaceId, "SQL 1");
        workspace.Tabs.Add(sql);
        await scope.Workspaces.UnlockAsync(workspace.WorkspaceId, Ct);

        var outcome = await Runner.WriteAsync(sql, new QuerySpec(Update, []), null, Ct);

        Assert.NotNull(outcome.Written);
    }

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();
}
