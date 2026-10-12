using FerretSharp.Core.Connections;
using FerretSharp.Core.Data;
using FerretSharp.Core.Schema;
using FerretSharp.UI.State;

namespace FerretSharp.UI.Tests.State;

/// <summary>
/// DDL of the table designer (WP-23) through <see cref="StatementRunner.RunDdlAsync"/>: statement by statement in the
/// workspace's session, stopping at the first failure, uncommitted changes rolled back first after the confirmation, the
/// schema reloaded and the history written as after DDL in the SQL editor.
/// </summary>
public sealed class DdlRunTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string First = """CREATE INDEX "APP_USER"."IX_A" ON "APP_USER"."KUNDEN" ("ID")""";
    private const string Second = """COMMENT ON TABLE "APP_USER"."KUNDEN" IS 'x'""";
    private const string Third = """ALTER TABLE "APP_USER"."KUNDEN" ADD ("FAX" VARCHAR2(30 BYTE))""";

    [Fact]
    public async Task All_statements_run_in_order_the_schema_is_reloaded_and_the_history_written()
    {
        await using var app = new TestApp();
        var profile = TestApp.Profile("Test");
        var scope = await app.OpenAsync(profile);
        var workspace = scope.Workspaces.Active!.Id;
        var started = new List<string>();
        (DdlRunResult Result, Notice? Notice)? ended = null;

        var result = await app.Lifecycle.Statements.RunDdlAsync(workspace, [First, Second], null, started.Add, r => ended = (r, app.Shell.Notice), Ct);

        // R4a: also a run that succeeded is told before the schema is reloaded – the designer drops its session then.
        Assert.Equal((result, (Notice?)null), ended);
        Assert.True(result.Succeeded);
        Assert.Equal([First, Second], result.Ran);
        Assert.Equal([First, Second], app.RanDdl);
        Assert.Equal([First, Second], started);
        Assert.Equal("Schema geändert – Explorer und offene Tabs zeigen den neuen Stand.", app.Shell.Notice?.Text);
        var history = await app.History.LoadAsync(profile.Id, Ct);
        Assert.Equal([Second, First], history.Select(h => h.Sql)); // newest first
        Assert.All(history, h => Assert.True(h.Ddl));
    }

    /// <summary>
    /// R4a: a statement the schema path refuses stopped the run with an unhandled programming error, after the statements
    /// before it had run (DDL commits each one). All are checked first; nothing runs, nothing is rolled back.
    /// </summary>
    [Fact]
    public async Task A_statement_the_schema_path_refuses_stops_the_run_before_anything_ran()
    {
        await using var app = new TestApp();
        var scope = await app.OpenAsync(TestApp.Profile("Test"));
        var pending = app.AddPendingWork(scope);
        const string twoStatements = """ALTER TABLE "APP_USER"."KUNDEN" ADD CHECK (STATUS IN ('A','B');)""";
        DdlRunResult? stopped = null;

        var result = await app.Lifecycle.Statements.RunDdlAsync(scope.Workspaces.Active!.Id, [First, twoStatements], app.WorkspaceOf(scope), null, r => stopped = r, Ct);

        Assert.Same(result, stopped);
        Assert.Empty(result.Ran);
        Assert.Equal(twoStatements, result.Failed);
        Assert.Equal("Das sind mehrere Anweisungen – ein „;“ oder eine Leerzeile darin beendet die Anweisung dort.", result.Error?.Message);
        Assert.Empty(app.RanDdl);
        Assert.Equal(1, pending.Changes!.PendingCount); // not discarded: nothing was going to run
    }

    [Fact]
    public async Task The_first_failure_stops_the_rest_and_names_who_holds_the_table()
    {
        await using var app = new TestApp();
        var profile = TestApp.Profile("Test");
        var scope = await app.OpenAsync(profile);
        var holder = new LockHolder(42, "APP_USER", "jdoe", "PC-1234", "MeineApp.exe", "FerretSharp", "Workspace 2", null);
        app.LockHolders = [holder];
        app.FailDdl = sql => sql == Second ? new DatabaseException("Sperre …", "ORA-00054") : null;

        (DdlRunResult Result, Notice? Notice)? stopped = null;
        var result = await app.Lifecycle.Statements.RunDdlAsync(scope.Workspaces.Active!.Id, [First, Second, Third], null, null, r => stopped = (r, app.Shell.Notice), Ct);

        // Told before the schema is reloaded (which may rebuild the designer's tab), with the same result.
        Assert.Equal((result, (Notice?)null), stopped);
        Assert.Equal(3, result.Total);
        Assert.False(result.Succeeded);
        Assert.Equal([First], result.Ran);
        Assert.Equal(Second, result.Failed);
        Assert.Equal("ORA-00054", result.Error?.ErrorCode);
        Assert.Equal([holder], result.Lock?.Holders);
        Assert.Equal([First], app.RanDdl); // the third did not run
        Assert.NotNull(app.Shell.Notice); // what ran changed the schema: reloaded
        var history = await app.History.LoadAsync(profile.Id, Ct);
        Assert.Equal([(Second, false), (First, true)], history.Select(h => (h.Sql, h.Ddl)));
        Assert.NotNull(history[0].Error);
    }

    /// <summary>
    /// WP-26: a trigger that does not compile is created all the same (disabled). The run stops there with its message, and
    /// the schema is reloaded although no statement succeeded – the explorer shows the new, invalid trigger.
    /// </summary>
    [Fact]
    public async Task A_trigger_that_does_not_compile_stops_the_run_and_still_reloads_the_schema()
    {
        await using var app = new TestApp();
        var scope = await app.OpenAsync(TestApp.Profile("Test"));
        const string trigger = """CREATE OR REPLACE TRIGGER "APP_USER"."KUNDEN_AUD" AFTER INSERT ON "APP_USER"."KUNDEN" FOR EACH ROW BEGIN x; END;""";
        var compileError = new TriggerCompileException("APP_USER", "KUNDEN_AUD",
            [new PlSqlError(PlSqlPart.Spec, 1, 99, "PLS-00201: Bezeichner 'X' muss deklariert werden", false)], isEnabled: false, statement: null);
        app.FailDdl = sql => sql == trigger ? compileError : null;

        var result = await app.Lifecycle.Statements.RunDdlAsync(scope.Workspaces.Active!.Id, [trigger, Second], null, null, null, Ct);

        Assert.Empty(result.Ran);
        Assert.Equal(trigger, result.Failed);
        Assert.Same(compileError, result.Error);
        Assert.Contains("1:99 PLS-00201", result.Error!.Display, StringComparison.Ordinal);
        Assert.Empty(app.RanDdl); // the comment after it did not run
        Assert.Equal("Schema geändert – Explorer und offene Tabs zeigen den neuen Stand.", app.Shell.Notice?.Text);
    }

    [Fact]
    public async Task Nothing_that_ran_reloads_nothing_and_a_cancel_is_no_error()
    {
        await using var app = new TestApp();
        var scope = await app.OpenAsync(TestApp.Profile("Test"));
        app.FailDdl = _ => new OperationCanceledException();

        var result = await app.Lifecycle.Statements.RunDdlAsync(scope.Workspaces.Active!.Id, [First, Second], null, null, null, Ct);

        Assert.True(result.Cancelled);
        Assert.Null(result.Error);
        Assert.Equal(First, result.Failed);
        Assert.Empty(result.Ran);
        Assert.Null(app.Shell.Notice);
    }

    /// <summary>
    /// R4a: the confirmation discarded every pending edit of the workspace, although DDL commits only the transaction. Now
    /// it rolls back the transaction: written grid changes become pending again, pending ones stay.
    /// </summary>
    [Fact]
    public async Task The_open_transaction_is_rolled_back_first_and_pending_edits_stay()
    {
        await using var app = new TestApp();
        var scope = await app.OpenAsync(TestApp.Profile("Test"));
        var tab = app.AddPendingWork(scope);
        var workspaceId = scope.Workspaces.Active!.Id;
        Assert.True(await app.Editing.FlushAsync(app.WorkspaceOf(scope)));
        tab.Changes!.AddRow();
        var (workspace, summary) = app.Lifecycle.OpenTransactionOf(workspaceId)!.Value;
        Assert.Equal((1, 1), (summary.Pending, summary.Flushed));

        var result = await app.Lifecycle.Statements.RunDdlAsync(workspaceId, [First], workspace, null, null, Ct);

        Assert.True(result.Succeeded);
        Assert.Equal((2, 0), (tab.Changes.PendingCount, tab.Changes.FlushedCount));
        Assert.Null(app.Lifecycle.OpenTransactionOf(workspaceId));
        Assert.Equal([First], app.RanDdl);
    }

    [Fact]
    public async Task Pending_edits_alone_are_no_open_transaction_and_stay()
    {
        await using var app = new TestApp();
        var scope = await app.OpenAsync(TestApp.Profile("Test"));
        var workspaceId = scope.Workspaces.Active!.Id;
        Assert.Null(app.Lifecycle.OpenTransactionOf(workspaceId));
        var tab = app.AddPendingWork(scope);

        Assert.Null(app.Lifecycle.OpenTransactionOf(workspaceId));
        Assert.True((await app.Lifecycle.Statements.RunDdlAsync(workspaceId, [First], null, null, null, Ct)).Succeeded);
        Assert.Equal(1, tab.Changes!.PendingCount);
    }
}
