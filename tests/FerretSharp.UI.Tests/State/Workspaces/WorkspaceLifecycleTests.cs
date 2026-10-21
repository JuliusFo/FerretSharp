using FerretSharp.Core.Connections;
using FerretSharp.Core.Resources;
using FerretSharp.UI.Resources;
using FerretSharp.UI.State;

namespace FerretSharp.UI.Tests.State;

/// <summary>
/// What the lifecycle asks before it changes a workspace's transaction (R2, WP-10, WP-24): a commit on Prod only after the
/// confirmation, a rollback after its question, leaving with uncommitted work after commit/discard – and stopping when
/// that fails –, locking again, giving up a session, the pending changes as SQL.
/// </summary>
public sealed class WorkspaceLifecycleTests : IAsyncDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly TestApp _app = new();

    private WorkspaceLifecycle Lifecycle => _app.Lifecycle;

    private static async Task<FakeDataEditor> EditorAsync(ConnectionScope scope) =>
        (FakeDataEditor)await scope.Workspaces.GetEditorAsync(scope.Workspaces.Active!.Id, Ct);

    /// <summary>Prod as it opens (locked), then unlocked through the lifecycle like the unlock dialog does.</summary>
    private async Task<ConnectionScope> OpenUnlockedProdAsync()
    {
        var scope = await _app.OpenAsync(TestApp.Profile("ERP Prod", ConnectionKind.Prod));
        var id = scope.Workspaces.Active!.Id;
        Assert.False(scope.Workspaces.IsWritable(id));

        await Lifecycle.UnlockAsync(id);

        Assert.True(scope.Workspaces.IsWritable(id));
        Assert.Equal(TextFormat.Format(ShellText.Lifecycle_Unlocked, Lifecycle.WorkspaceName(id)), _app.Shell.Notice?.Text);
        return scope;
    }

    [Fact]
    public async Task A_commit_on_prod_waits_for_the_confirmation()
    {
        var scope = await OpenUnlockedProdAsync();
        var workspace = _app.WorkspaceOf(scope);
        var tab = _app.AddPendingWork(scope);
        Assert.True(Lifecycle.IsProd);

        await Lifecycle.RequestCommitAsync(workspace);
        Assert.Same(workspace, Lifecycle.ConfirmCommit);
        Assert.Equal(1, tab.Changes!.PendingCount); // nothing written before the answer

        Lifecycle.CancelConfirmation();
        Assert.Null(Lifecycle.ConfirmCommit);
        Assert.Equal(1, tab.Changes.PendingCount);

        await Lifecycle.RequestCommitAsync(workspace);
        await Lifecycle.CommitAsync(workspace, _app.Editing.SummaryOf(workspace).Actions);

        Assert.Null(Lifecycle.ConfirmCommit);
        Assert.Equal((0, 0), (tab.Changes.PendingCount, tab.Changes.FlushedCount));
        Assert.Equal(GridText.Editing_Committed, _app.Shell.Notice?.Text);
    }

    [Fact]
    public async Task A_commit_elsewhere_runs_at_once()
    {
        var scope = await _app.OpenAsync(TestApp.Profile("ERP Test"));
        var tab = _app.AddPendingWork(scope);

        await Lifecycle.RequestCommitAsync(_app.WorkspaceOf(scope));

        Assert.False(Lifecycle.IsProd);
        Assert.Null(Lifecycle.ConfirmCommit);
        Assert.Equal((0, 0), (tab.Changes!.PendingCount, tab.Changes.FlushedCount));
    }

    [Fact]
    public async Task A_rollback_asks_first()
    {
        var scope = await _app.OpenAsync(TestApp.Profile("ERP Test"));
        var workspace = _app.WorkspaceOf(scope);
        var tab = _app.AddPendingWork(scope);
        Assert.True(await _app.Editing.FlushAsync(workspace));

        Lifecycle.RequestRollback(workspace);
        Assert.Same(workspace, Lifecycle.ConfirmRollback);
        Assert.Equal(1, tab.Changes!.FlushedCount);

        await Lifecycle.RollbackAsync(workspace);

        Assert.Null(Lifecycle.ConfirmRollback);
        Assert.Equal((0, 0), (tab.Changes.PendingCount, tab.Changes.FlushedCount));
        Assert.Equal(GridText.Editing_RolledBack, _app.Shell.Notice?.Text);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Leaving_commits_or_discards_the_work_and_then_continues(bool commit)
    {
        var scope = await _app.OpenAsync(TestApp.Profile("ERP Test"));
        var tab = _app.AddPendingWork(scope);
        var committed = false;
        _app.OnCommit = () => { committed = true; return Task.CompletedTask; };
        var continued = 0;

        await Lifecycle.GuardAsync("Verbindung trennen", [scope.Workspaces.Active!.Id], () => { continued++; return Task.CompletedTask; });
        Assert.Equal([scope.Workspaces.Active!.Id], Lifecycle.PendingLeave!.WorkspaceIds);
        Assert.Equal(0, continued);

        await Lifecycle.LeaveAsync(commit);

        Assert.Equal((1, commit), (continued, committed));
        Assert.Null(Lifecycle.PendingLeave);
        Assert.Equal((0, 0), (tab.Changes!.PendingCount, tab.Changes.FlushedCount));
    }

    [Fact]
    public async Task Leaving_stops_when_discarding_fails_and_keeps_the_work()
    {
        var scope = await _app.OpenAsync(TestApp.Profile("ERP Test"));
        var workspace = _app.WorkspaceOf(scope);
        var tab = _app.AddPendingWork(scope);
        Assert.True(await _app.Editing.FlushAsync(workspace));
        (await EditorAsync(scope)).FailRollback = new DatabaseException("ORA-00600: interner Fehler", "ORA-00600");
        var continued = false;
        await Lifecycle.GuardAsync("Verbindung trennen", [workspace], () => { continued = true; return Task.CompletedTask; });

        await Lifecycle.LeaveAsync(commit: false);

        Assert.False(continued); // nothing left behind half done
        Assert.Null(Lifecycle.PendingLeave);
        Assert.Equal(1, tab.Changes!.FlushedCount);
        Assert.Equal("ORA-00600", _app.Shell.ErrorDetails?.ErrorCode);
    }

    [Fact]
    public async Task Locking_again_asks_about_the_changes_first()
    {
        var scope = await OpenUnlockedProdAsync();
        var id = scope.Workspaces.Active!.Id;
        _app.AddPendingWork(scope);

        await Lifecycle.LockAsync(id);
        Assert.NotNull(Lifecycle.PendingLeave);
        Assert.True(scope.Workspaces.IsWritable(id));

        await Lifecycle.LeaveAsync(commit: false);

        Assert.False(scope.Workspaces.IsWritable(id));
        Assert.Equal(TextFormat.Format(ShellText.Lifecycle_Locked, Lifecycle.WorkspaceName(id)), _app.Shell.Notice?.Text);
    }

    [Fact]
    public async Task The_last_open_workspace_is_not_closed()
    {
        var scope = await _app.OpenAsync(TestApp.Profile("ERP Test"));
        var id = scope.Workspaces.Active!.Id;
        _app.AddPendingWork(scope);

        await Lifecycle.CloseWorkspaceAsync(id);

        Assert.Null(Lifecycle.PendingLeave);
        Assert.True(scope.Workspaces.Find(id)!.IsOpen);
    }

    [Fact]
    public async Task Giving_up_the_session_makes_the_written_changes_pending_again()
    {
        var scope = await _app.OpenAsync(TestApp.Profile("ERP Test"));
        var workspace = _app.WorkspaceOf(scope);
        var tab = _app.AddPendingWork(scope);
        Assert.True(await _app.Editing.FlushAsync(workspace));

        await Lifecycle.ResetSessionAsync(workspace.WorkspaceId);

        Assert.Equal((1, 0), (tab.Changes!.PendingCount, tab.Changes.FlushedCount));
        Assert.Equal(TextFormat.Format(ShellText.Lifecycle_SessionReset, Lifecycle.WorkspaceName(workspace.WorkspaceId)), _app.Shell.Notice?.Text);
    }

    [Fact]
    public async Task The_pending_changes_show_as_sql()
    {
        var scope = await _app.OpenAsync(TestApp.Profile("ERP Test"));
        var workspace = _app.WorkspaceOf(scope);

        Lifecycle.ShowPendingSql(workspace);
        Assert.Equal(GridText.Editing_NoPending, Lifecycle.PendingSql);

        _app.AddPendingWork(scope);
        Lifecycle.ShowPendingSql(workspace);
        Assert.StartsWith("-- APP_USER.KUNDEN", Lifecycle.PendingSql);
        Assert.Contains("INSERT INTO", Lifecycle.PendingSql);

        Lifecycle.ClosePendingSql();
        Assert.Null(Lifecycle.PendingSql);
    }

    [Fact]
    public async Task Answers_without_a_question_change_nothing()
    {
        var scope = await _app.OpenAsync(TestApp.Profile("ERP Test"));
        var tab = _app.AddPendingWork(scope);

        await Lifecycle.LeaveAsync(commit: false);
        await Lifecycle.RedoConfirmedAsync();
        await Lifecycle.ResolveRedoRowsAsync(keep: false);
        Lifecycle.CloseTabsConfirmed(keepLosing: false);

        Assert.Equal(1, tab.Changes!.PendingCount);
        Assert.Contains(tab, _app.WorkspaceOf(scope).Tabs);
    }

    /// <summary>
    /// Review 3.44.0, A5: closing the window or restarting into an update cancelled a running export without a word. Quitting
    /// asks; "cancel exports and quit" cancels them, lets them remove their files, then asks about uncommitted work as usual.
    /// </summary>
    [Fact]
    public async Task Quitting_while_an_export_runs_asks_and_cancels_it_first()
    {
        var scope = await _app.OpenAsync(TestApp.Profile("ERP Test"));
        _app.AddPendingWork(scope);
        var export = _app.Shell.Exports.Start("KUNDEN.xlsx", null, workspaceId: _app.WorkspaceOf(scope).WorkspaceId);
        export.Token.Register(() => _app.Shell.Exports.End(export)); // as the export does when its read stops
        var approved = 0;

        Assert.False(Lifecycle.CanExit);
        await Lifecycle.RequestExitAsync(() => approved++);
        Assert.Equal([export], Lifecycle.ConfirmExitExports);
        Assert.Null(Lifecycle.PendingLeave);

        Lifecycle.CancelExitExports(); // "keep exporting"
        Assert.Equal([export], _app.Shell.Exports.Running);

        await Lifecycle.RequestExitAsync(() => approved++);
        await Lifecycle.ExitCancellingExportsAsync();

        Assert.Empty(_app.Shell.Exports.Running);
        Assert.Null(Lifecycle.ConfirmExitExports);
        Assert.NotNull(Lifecycle.PendingLeave); // the pending change is asked about next
        await Lifecycle.LeaveAsync(commit: false);
        Assert.Equal(1, approved);
    }

    public ValueTask DisposeAsync() => _app.DisposeAsync();
}
