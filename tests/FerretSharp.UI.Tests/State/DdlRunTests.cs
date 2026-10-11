using FerretSharp.Core.Connections;
using FerretSharp.Core.Data;
using FerretSharp.UI.State;

namespace FerretSharp.UI.Tests.State;

/// <summary>
/// DDL of the table designer (WP-23) through <see cref="WorkspaceLifecycle.RunDdlAsync"/>: statement by statement in the
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

        var result = await app.Lifecycle.RunDdlAsync(workspace, [First, Second], null, started.Add, null, Ct);

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

        var result = await app.Lifecycle.RunDdlAsync(scope.Workspaces.Active!.Id, [First, twoStatements], app.WorkspaceOf(scope), null, r => stopped = r, Ct);

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
        var result = await app.Lifecycle.RunDdlAsync(scope.Workspaces.Active!.Id, [First, Second, Third], null, null, r => stopped = (r, app.Shell.Notice), Ct);

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

    [Fact]
    public async Task Nothing_that_ran_reloads_nothing_and_a_cancel_is_no_error()
    {
        await using var app = new TestApp();
        var scope = await app.OpenAsync(TestApp.Profile("Test"));
        app.FailDdl = _ => new OperationCanceledException();

        var result = await app.Lifecycle.RunDdlAsync(scope.Workspaces.Active!.Id, [First, Second], null, null, null, Ct);

        Assert.True(result.Cancelled);
        Assert.Null(result.Error);
        Assert.Equal(First, result.Failed);
        Assert.Empty(result.Ran);
        Assert.Null(app.Shell.Notice);
    }

    [Fact]
    public async Task Uncommitted_changes_are_rolled_back_first_when_the_confirmation_says_so()
    {
        await using var app = new TestApp();
        var scope = await app.OpenAsync(TestApp.Profile("Test"));
        var tab = app.AddPendingWork(scope);
        var workspaceId = scope.Workspaces.Active!.Id;
        var (workspace, summary) = app.Lifecycle.OpenWorkOf(workspaceId)!.Value;
        Assert.Equal(1, summary.Pending);

        var result = await app.Lifecycle.RunDdlAsync(workspaceId, [First], workspace, null, null, Ct);

        Assert.True(result.Succeeded);
        Assert.Equal(0, tab.Changes!.PendingCount);
        Assert.Null(app.Lifecycle.OpenWorkOf(workspaceId));
        Assert.Equal([First], app.RanDdl);
    }

    [Fact]
    public async Task A_workspace_without_uncommitted_changes_has_no_open_work()
    {
        await using var app = new TestApp();
        var scope = await app.OpenAsync(TestApp.Profile("Test"));

        Assert.Null(app.Lifecycle.OpenWorkOf(scope.Workspaces.Active!.Id));
    }
}
