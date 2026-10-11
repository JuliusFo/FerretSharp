using FerretSharp.Core.Connections;
using FerretSharp.Core.Data;
using FerretSharp.Core.Query;
using FerretSharp.UI.State;

namespace FerretSharp.UI.Tests.State;

/// <summary>
/// R4b: the one way statements of the SQL editor, the designer and the LINQ console run – read, write and DDL with the
/// SQL history, the change overview, lock holders and what follows afterwards.
/// </summary>
public sealed class StatementRunnerTests : IAsyncDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly TestApp _app = new();

    private StatementRunner Runner => _app.Lifecycle.Statements;

    private static readonly SqlVariable Id = new("id", SqlVariableType.Number, "4711");

    private static StatementLog Log(string sql) => new(sql, [Id]);

    [Fact]
    public async Task A_query_reads_its_first_page_and_goes_into_the_history_with_its_variables()
    {
        var profile = TestApp.Profile("Test");
        var scope = await _app.OpenAsync(profile);
        QuerySpec? read = null;
        _app.ReadSql = (query, _) =>
        {
            read = query;
            return Task.FromResult(new SqlPage([], [[1m], [2m]], IsLastPage: false, TimeSpan.FromMilliseconds(42)));
        };
        var query = new QuerySpec("SELECT * FROM kunden WHERE id = :id", []);

        var outcome = await Runner.ReadAsync(scope.Workspaces.Active!.Id, query, 500, Log("SELECT * FROM kunden WHERE id = :id"), Ct);

        Assert.Same(query, read);
        Assert.Equal(2, outcome.FirstPage?.Rows.Count);
        var entry = Assert.Single(outcome.History!);
        Assert.Equal((2, true, false, TimeSpan.FromMilliseconds(42)), (entry.Rows, entry.MoreRows, entry.Changed, entry.Elapsed));
        Assert.Equal([Id], entry.Variables);
        Assert.Equal([query.Sql], (await _app.History.LoadAsync(profile.Id, Ct)).Select(h => h.Sql));
    }

    [Fact]
    public async Task On_prod_the_history_keeps_no_values_and_without_a_log_nothing_goes_into_it()
    {
        var profile = TestApp.Profile("Prod", ConnectionKind.Prod);
        var scope = await _app.OpenAsync(profile);
        var workspace = scope.Workspaces.Active!.Id;

        await Runner.ReadAsync(workspace, new QuerySpec("SELECT 1 FROM dual", []), 500, null, Ct); // the LINQ console
        var logged = await Runner.ReadAsync(workspace, new QuerySpec("SELECT :id FROM dual", []), 500, Log("SELECT :id FROM dual"), Ct);

        Assert.Equal("", Assert.Single(Assert.Single(logged.History!).Variables).Value);
        Assert.Single(await _app.History.LoadAsync(profile.Id, Ct));
    }

    [Fact]
    public async Task A_write_goes_into_the_transaction_and_the_change_overview_learns_its_tab()
    {
        var scope = await _app.OpenAsync(TestApp.Profile("Test"));
        var workspace = _app.WorkspaceOf(scope);
        var sql = new SqlTab(workspace.WorkspaceId, "SQL 1");
        workspace.Tabs.Add(sql);
        var table = _app.AddPendingWork(scope);
        var reloaded = new List<WorkspaceTab>();
        _app.Shell.TabCommandRequested += (tab, command) =>
        {
            if (command == TabCommand.Reload)
            {
                reloaded.Add(tab);
            }
        };

        var outcome = await Runner.WriteAsync(sql, new QuerySpec("UPDATE kunden SET name = 'x'", []), Log("UPDATE kunden SET name = 'x'"), Ct);

        var written = Assert.IsType<WriteAction>(outcome.Written);
        Assert.Same(sql, _app.Editing.SourceOf(workspace, written));
        Assert.Equal(scope.Workspaces.TransactionOf(workspace.WorkspaceId)?.StartedAt, outcome.Transaction);
        Assert.Equal(WriteFate.Open, WorkspaceEditing.FateOf(scope.Workspaces, workspace.WorkspaceId, written.Id, outcome.Transaction));
        var entry = Assert.Single(outcome.History!);
        Assert.Equal((1, true), (entry.Rows, entry.Changed));
        Assert.Empty(reloaded); // the caller says when its statements are done

        await Runner.FinishAsync(workspace.WorkspaceId, wrote: true, changedSchema: false);

        Assert.Equal([table], reloaded);
        Assert.Null(_app.Shell.Notice); // no DDL: the schema stays
    }

    [Fact]
    public async Task Ddl_changes_the_schema_and_finishing_reloads_it()
    {
        var profile = TestApp.Profile("Test");
        var scope = await _app.OpenAsync(profile);
        const string ddl = """ALTER TABLE "APP_USER"."KUNDEN" ADD ("FAX" VARCHAR2(30 BYTE))""";

        var outcome = await Runner.DdlAsync(scope.Workspaces.Active!.Id, ddl, StatementLog.Of(ddl), Ct);

        Assert.True(outcome.SchemaChanged);
        Assert.Equal([ddl], _app.RanDdl);
        Assert.True(Assert.Single(outcome.History!).Ddl);

        await Runner.FinishAsync(scope.Workspaces.Active!.Id, wrote: false, changedSchema: true);
        Assert.Equal("Schema geändert – Explorer und offene Tabs zeigen den neuen Stand.", _app.Shell.Notice?.Text);
    }

    [Fact]
    public async Task Ddl_on_a_table_another_session_holds_names_who_it_is()
    {
        var scope = await _app.OpenAsync(TestApp.Profile("Test"));
        var holder = new LockHolder(42, "APP_USER", "jdoe", "PC-1234", "MeineApp.exe", null, null, null);
        _app.LockHolders = [holder];
        _app.FailDdl = _ => new DatabaseException("ORA-00054: Ressource belegt", "ORA-00054");
        const string ddl = """ALTER TABLE "APP_USER"."KUNDEN" DROP COLUMN "FAX" """;

        var outcome = await Runner.DdlAsync(scope.Workspaces.Active!.Id, ddl.TrimEnd(), StatementLog.Of(ddl.TrimEnd()), Ct);

        Assert.False(outcome.SchemaChanged);
        Assert.Equal("ORA-00054", outcome.Error?.ErrorCode);
        Assert.Equal(TestApp.Kunden.Ref, outcome.Lock?.Table);
        Assert.Equal([holder], outcome.Lock?.Holders);
        var entry = Assert.Single(outcome.History!);
        Assert.False(entry.Ddl);
        Assert.NotNull(entry.Error);
    }

    [Fact]
    public async Task An_error_goes_into_the_history_a_refusal_and_a_cancel_do_not()
    {
        var profile = TestApp.Profile("Test");
        var scope = await _app.OpenAsync(profile);
        var workspace = scope.Workspaces.Active!.Id;
        var query = new QuerySpec("SELECT * FROM gibt_es_nicht", []);

        _app.ReadSql = (_, _) => Task.FromException<SqlPage>(new DatabaseException("ORA-00942: Tabelle oder View nicht vorhanden", "ORA-00942"));
        var failed = await Runner.ReadAsync(workspace, query, 500, Log(query.Sql), Ct);
        _app.ReadSql = (_, _) => Task.FromException<SqlPage>(new RefusedException("Nicht erlaubt."));
        var refused = await Runner.ReadAsync(workspace, query, 500, Log(query.Sql), Ct);
        _app.ReadSql = (_, _) => Task.FromCanceled<SqlPage>(new CancellationToken(canceled: true));
        var cancelled = await Runner.ReadAsync(workspace, query, 500, Log(query.Sql), Ct);

        Assert.Equal(("ORA-00942", false, false), (failed.Error?.ErrorCode, failed.Refused, failed.Cancelled));
        Assert.Equal(("Nicht erlaubt.", true), (refused.Error?.Message, refused.Refused));
        Assert.Null(refused.History);
        Assert.True(cancelled.Cancelled);
        Assert.Null(cancelled.Error);
        Assert.Null(cancelled.History);
        Assert.NotNull(Assert.Single(await _app.History.LoadAsync(profile.Id, Ct)).Error);
    }

    [Fact]
    public async Task Discarding_the_transaction_before_ddl_rolls_back_the_writes_and_keeps_pending_edits()
    {
        var scope = await _app.OpenAsync(TestApp.Profile("Test"));
        var workspace = _app.WorkspaceOf(scope);
        var tab = _app.AddPendingWork(scope);
        Assert.True(await _app.Editing.FlushAsync(workspace));
        tab.Changes!.AddRow();

        Assert.True(await Runner.DiscardTransactionAsync(workspace));

        Assert.Equal((2, 0), (tab.Changes.PendingCount, tab.Changes.FlushedCount));
        Assert.Null(_app.Lifecycle.OpenTransactionOf(workspace.WorkspaceId));
    }

    public ValueTask DisposeAsync() => _app.DisposeAsync();
}
