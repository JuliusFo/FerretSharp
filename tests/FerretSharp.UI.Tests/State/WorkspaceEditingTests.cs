using FerretSharp.Core.Connections;
using FerretSharp.Core.Data;
using FerretSharp.Core.Query;
using FerretSharp.Core.Schema;
using FerretSharp.UI.State;

namespace FerretSharp.UI.Tests.State;

public sealed class WorkspaceEditingTests : IAsyncDisposable
{
    private readonly TestApp _app = new();

    /// <summary>
    /// Writing is busy per workspace (WP-24): before R3a one flag covered all of them, and a slow commit on one
    /// connection made Ctrl+S or Commit on another do nothing without a word.
    /// </summary>
    [Fact]
    public async Task A_running_commit_blocks_only_its_own_workspace()
    {
        var test = await _app.OpenAsync(TestApp.Profile("Test"));
        var dev = await _app.OpenAsync(TestApp.Profile("Dev"));
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = new TaskCompletionSource();
        _app.OnCommit = () => { reached.SetResult(); return held.Task; };

        var slow = _app.Editing.CommitAsync(_app.WorkspaceOf(test));

        Assert.True(_app.Editing.IsBusy(test.Workspaces.Active!.Id));
        Assert.False(_app.Editing.IsBusy(dev.Workspaces.Active!.Id));

        // The workspace session opens on the thread pool, so the commit reaches the database only later. Without the wait,
        // OnCommit was mostly null by then: both commits ran at once on pool threads, and the busy set (one UI thread in the
        // app) lost an entry.
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        _app.OnCommit = null;
        Assert.True(await _app.Editing.CommitAsync(_app.WorkspaceOf(dev)));
        Assert.True(_app.Editing.IsBusy(test.Workspaces.Active!.Id));
        Assert.False(await _app.Editing.RollbackAsync(_app.WorkspaceOf(test))); // review 3.31.0, A27: said so, not silently
        Assert.StartsWith("Änderungen dieses Workspaces werden gerade geschrieben", _app.Shell.Notice?.Text);

        held.SetResult();
        Assert.True(await slow);
        Assert.False(_app.Editing.IsBusy(test.Workspaces.Active!.Id));
    }

    /// <summary>
    /// R4a: a rollback that failed with the connection left the grid writes marked as written; a later commit said
    /// "committed" for data Oracle had rolled back.
    /// </summary>
    [Fact]
    public async Task Commit_without_the_transaction_that_held_the_writes_is_refused_and_they_are_pending_again()
    {
        var scope = await _app.OpenAsync(TestApp.Profile("Test"));
        var workspace = _app.WorkspaceOf(scope);
        var tab = _app.AddPendingWork(scope);
        Assert.True(await _app.Editing.FlushAsync(workspace));
        var committed = false;
        _app.OnCommit = () => { committed = true; return Task.CompletedTask; };
        _app.Editors.ForEach(e => e.Ended = true);

        Assert.False(await _app.Editing.CommitAsync(workspace));

        Assert.False(committed);
        Assert.Equal((1, 0), (tab.Changes!.PendingCount, tab.Changes.FlushedCount));
        Assert.StartsWith("Nicht committet: Die Transaktion endete ohne Commit", _app.Shell.Notice?.Text);
    }

    /// <summary>Review 3.31.0, A28: the check above only looked at open tabs – a closed tab's writes were lost unnoticed.</summary>
    [Fact]
    public async Task Commit_without_the_transaction_is_refused_also_for_writes_of_a_closed_tab()
    {
        var scope = await _app.OpenAsync(TestApp.Profile("Test"));
        var workspace = _app.WorkspaceOf(scope);
        var tab = _app.AddPendingWork(scope);
        Assert.True(await _app.Editing.FlushAsync(workspace));
        workspace.Tabs.Remove(tab);
        var committed = false;
        _app.OnCommit = () => { committed = true; return Task.CompletedTask; };
        _app.Editors.ForEach(e => e.Ended = true);

        Assert.False(await _app.Editing.CommitAsync(workspace));

        Assert.False(committed);
        Assert.StartsWith("Nicht committet: Die Transaktion endete ohne Commit", _app.Shell.Notice?.Text);
    }

    /// <summary>Review 3.31.0, A31: a new write ends redo, but the history kept the writes taken back until the transaction ended.</summary>
    [Fact]
    public async Task A_new_write_forgets_the_writes_taken_back_before()
    {
        var scope = await _app.OpenAsync(TestApp.Profile("Test"));
        var (workspace, tab) = AddTab(scope);
        tab.Changes!.SetValue(Row(1, "Meier"), 1, "Müller");
        Assert.True(await _app.Editing.FlushAsync(workspace));
        Assert.True(await _app.Editing.UndoLastAsync(workspace));
        var undone = Assert.Single(_app.Editing.SummaryOf(workspace).Undone);
        tab.Changes.DiscardPending();

        tab.Changes.SetValue(Row(2, "Lang"), 1, "Kurz");
        Assert.True(await _app.Editing.FlushAsync(workspace));

        Assert.Empty(_app.Editing.SummaryOf(workspace).Undone);
        Assert.Null(_app.Editing.History.Undone(workspace.WorkspaceId, undone.Id));
    }

    [Fact]
    public async Task Undoing_a_write_of_a_closed_tab_says_its_changes_are_discarded()
    {
        var scope = await _app.OpenAsync(TestApp.Profile("Test"));
        var workspace = _app.WorkspaceOf(scope);
        var tab = _app.AddPendingWork(scope);
        Assert.True(await _app.Editing.FlushAsync(workspace));
        workspace.Tabs.Remove(tab);

        Assert.True(await _app.Editing.UndoLastAsync(workspace));

        Assert.StartsWith("Zurückgenommen:", _app.Shell.Notice?.Text);
        Assert.EndsWith("der Tab ist geschlossen, die Änderungen sind verworfen.", _app.Shell.Notice?.Text);
    }

    /// <summary>R4a: on Prod, a statement of another tab finishing after the confirmation opened was committed unseen.</summary>
    [Fact]
    public async Task Commit_after_a_confirmation_refuses_writes_it_did_not_show()
    {
        var scope = await _app.OpenAsync(TestApp.Profile("Prod", ConnectionKind.Prod));
        var workspace = _app.WorkspaceOf(scope);
        var id = scope.Workspaces.Active!.Id;
        await scope.Workspaces.UnlockAsync(id, TestContext.Current.CancellationToken); // Prod is locked until unlocked (WP-10)
        await scope.Workspaces.ExecuteAsync(id, new QuerySpec("UPDATE kunden SET name = 'a'", []), TestContext.Current.CancellationToken);
        var shown = _app.Editing.SummaryOf(workspace).Actions;
        await scope.Workspaces.ExecuteAsync(id, new QuerySpec("UPDATE kunden SET name = 'b'", []), TestContext.Current.CancellationToken);
        var committed = false;
        _app.OnCommit = () => { committed = true; return Task.CompletedTask; };

        Assert.False(await _app.Editing.CommitAsync(workspace, shown));
        Assert.False(committed);

        Assert.True(await _app.Editing.CommitAsync(workspace, _app.Editing.SummaryOf(workspace).Actions));
        Assert.True(committed);
    }

    // ---- R4b: what the tests did not cover yet ----

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

    private static LockConflictException Locked(PendingOperation operation) => new(operation, new DatabaseException("ORA-00054: Ressource belegt", "ORA-00054"));

    [Fact]
    public async Task A_commit_oracle_rolled_back_makes_the_grid_writes_pending_again()
    {
        var scope = await _app.OpenAsync(TestApp.Profile("Test"));
        var (workspace, tab) = AddTab(scope);
        tab.Changes!.SetValue(Row(1, "Meier"), 1, "Müller");
        Assert.True(await _app.Editing.FlushAsync(workspace));
        var write = _app.Editing.SummaryOf(workspace).Actions[0];
        _app.OnCommit = () =>
        {
            _app.Editors.ForEach(e => e.Ended = true); // a deferred constraint failed: Oracle rolled back
            return Task.FromException(new DatabaseException("ORA-02091: Transaktion zurückgesetzt", "ORA-02091"));
        };

        Assert.False(await _app.Editing.CommitAsync(workspace));

        Assert.Equal((1, 0), (tab.Changes.PendingCount, tab.Changes.FlushedCount));
        Assert.Equal("ORA-02091", _app.Shell.ErrorDetails?.ErrorCode);
        Assert.Null(_app.Editing.GridWriteOf(workspace.WorkspaceId, write.Id));
    }

    [Fact]
    public async Task A_failed_rollback_keeps_the_writes_so_it_can_be_tried_again()
    {
        var scope = await _app.OpenAsync(TestApp.Profile("Test"));
        var (workspace, tab) = AddTab(scope);
        tab.Changes!.SetValue(Row(1, "Meier"), 1, "Müller");
        Assert.True(await _app.Editing.FlushAsync(workspace));
        var editor = await EditorAsync(scope);
        editor.FailRollback = new DatabaseException("ORA-00600: interner Fehler", "ORA-00600");

        Assert.False(await _app.Editing.RollbackAsync(workspace));

        Assert.Equal((0, 1), (tab.Changes.PendingCount, tab.Changes.FlushedCount));
        Assert.Equal("ORA-00600", _app.Shell.ErrorDetails?.ErrorCode);
        Assert.True(_app.Editing.SummaryOf(workspace).CanUndo);

        editor.FailRollback = null;
        Assert.True(await _app.Editing.RollbackAsync(workspace));
        Assert.Equal((0, 0), (tab.Changes.PendingCount, tab.Changes.FlushedCount));
        Assert.False(_app.Editing.History.Knows(workspace.WorkspaceId));
    }

    [Fact]
    public async Task A_session_given_up_makes_the_grid_writes_pending_again_and_forgets_their_history()
    {
        var scope = await _app.OpenAsync(TestApp.Profile("Test"));
        var (workspace, tab) = AddTab(scope);
        tab.Changes!.SetValue(Row(1, "Meier"), 1, "Müller");
        Assert.True(await _app.Editing.FlushAsync(workspace));
        var statement = await (await EditorAsync(scope)).ExecuteAsync(new QuerySpec("UPDATE KUNDEN SET NAME = 'x'", []), CancellationToken.None);
        _app.Editing.NoteStatement(new SqlTab(workspace.WorkspaceId, "SQL 1"), statement);

        _app.Editing.SessionReset(workspace);

        Assert.Equal((1, 0), (tab.Changes.PendingCount, tab.Changes.FlushedCount));
        Assert.False(_app.Editing.History.Knows(workspace.WorkspaceId));
    }

    [Fact]
    public async Task A_lock_conflict_names_who_holds_the_row_and_retry_writes_once_the_other_session_is_done()
    {
        var scope = await _app.OpenAsync(TestApp.Profile("Test"));
        var (workspace, tab) = AddTab(scope);
        tab.Changes!.SetValue(Row(1, "Meier"), 1, "Müller");
        var editor = await EditorAsync(scope);
        var holder = new LockHolder(42, "APP_USER", "jdoe", "PC-1234", "MeineApp.exe", null, null, null);
        _app.LockHolders = [holder];
        editor.FailFlush = operations => Locked(operations[0]);

        Assert.False(await _app.Editing.FlushAsync(workspace));

        var problem = _app.Editing.Problem;
        Assert.IsType<LockConflictException>(problem?.Error);
        Assert.Equal([holder], problem!.Holders);
        Assert.False(problem.DuringCommit);
        Assert.Same(tab, problem.Tab);

        editor.FailFlush = null;
        await _app.Editing.ResolveAsync(ProblemChoice.Retry);

        Assert.Null(_app.Editing.Problem);
        Assert.Equal((0, 1), (tab.Changes.PendingCount, tab.Changes.FlushedCount));
    }

    [Fact]
    public async Task Overwrite_writes_the_changed_row_anyway()
    {
        var scope = await _app.OpenAsync(TestApp.Profile("Test"));
        var (workspace, tab) = AddTab(scope);
        var change = tab.Changes!.SetValue(Row(1, "Meier"), 1, "Müller").Change!;
        var editor = await EditorAsync(scope);
        var overwritten = new List<Guid>();
        editor.FailFlush = operations =>
        {
            if (editor.LastFlush!.Overwrite?.Contains(operations[0].Change.Id) == true)
            {
                overwritten.Add(operations[0].Change.Id);
                return null;
            }

            return new ConcurrencyConflictException(operations[0], []);
        };

        Assert.False(await _app.Editing.FlushAsync(workspace));
        await _app.Editing.ResolveAsync(ProblemChoice.Overwrite);

        Assert.Equal([change.Id], overwritten);
        Assert.Null(_app.Editing.Problem);
        Assert.Equal(1, tab.Changes.FlushedCount);
    }

    [Fact]
    public async Task Discard_drops_the_change_of_the_row_and_writes_the_rest()
    {
        var scope = await _app.OpenAsync(TestApp.Profile("Test"));
        var (workspace, tab) = AddTab(scope);
        var locked = tab.Changes!.SetValue(Row(1, "Meier"), 1, "Müller").Change!;
        tab.Changes.SetValue(Row(2, "Lang"), 1, "Kurz");
        var added = tab.Changes.AddRow();
        tab.Changes.SetValue(added, 1, "Neu");
        var editor = await EditorAsync(scope);
        editor.FailFlush = operations => operations.FirstOrDefault(o => o.Change == locked || o.Change == added) is { } operation ? Locked(operation) : null;

        Assert.False(await _app.Editing.FlushAsync(workspace));
        await _app.Editing.ResolveAsync(ProblemChoice.Discard); // the existing row: its pending change goes
        await _app.Editing.ResolveAsync(ProblemChoice.Discard); // the new row: it goes

        Assert.Null(_app.Editing.Problem);
        Assert.Null(tab.Changes.Find(locked.Key));
        Assert.Empty(tab.Changes.NewRows);
        Assert.Equal((0, 1), (tab.Changes.PendingCount, tab.Changes.FlushedCount));
    }

    [Fact]
    public async Task Cancel_leaves_the_changes_pending_and_a_commit_stopped_by_a_problem_goes_on_after_retry()
    {
        var scope = await _app.OpenAsync(TestApp.Profile("Test"));
        var (workspace, tab) = AddTab(scope);
        tab.Changes!.SetValue(Row(1, "Meier"), 1, "Müller");
        var editor = await EditorAsync(scope);
        editor.FailFlush = operations => Locked(operations[0]);
        var commits = 0;
        _app.OnCommit = () =>
        {
            commits++;
            return Task.CompletedTask;
        };

        Assert.False(await _app.Editing.CommitAsync(workspace));
        Assert.True(_app.Editing.Problem?.DuringCommit);
        await _app.Editing.ResolveAsync(ProblemChoice.Cancel);
        Assert.Null(_app.Editing.Problem);
        Assert.Equal((1, 0, 0), (tab.Changes.PendingCount, tab.Changes.FlushedCount, commits));

        Assert.False(await _app.Editing.CommitAsync(workspace));
        editor.FailFlush = null;
        await _app.Editing.ResolveAsync(ProblemChoice.Retry);

        Assert.Equal(1, commits);
        Assert.Equal((0, 0), (tab.Changes.PendingCount, tab.Changes.FlushedCount));
    }

    [Fact]
    public async Task A_redo_stopped_by_a_problem_stays_redoable()
    {
        var scope = await _app.OpenAsync(TestApp.Profile("Test"));
        var (workspace, tab) = AddTab(scope);
        tab.Changes!.SetValue(Row(1, "Meier"), 1, "Müller");
        Assert.True(await _app.Editing.FlushAsync(workspace));
        Assert.True(await _app.Editing.UndoLastAsync(workspace));
        var undone = Assert.Single(_app.Editing.SummaryOf(workspace).Undone);
        var editor = await EditorAsync(scope);
        editor.FailFlush = operations => Locked(operations[0]);

        Assert.False(await _app.Editing.RedoGridAsync(workspace, undone.Id));

        Assert.NotNull(_app.Editing.Problem);
        Assert.Null(_app.Editing.RedoBlocker(workspace));
        Assert.NotNull(_app.Editing.History.Undone(workspace.WorkspaceId, undone.Id));
        Assert.Equal(1, tab.Changes.PendingCount);

        await _app.Editing.ResolveAsync(ProblemChoice.Cancel);
        editor.FailFlush = null;
        Assert.True(await _app.Editing.RedoGridAsync(workspace, undone.Id));
        Assert.Equal((0, 1), (tab.Changes.PendingCount, tab.Changes.FlushedCount));
        Assert.Equal(undone.Id, Assert.Single(editor.Actions).RedoOf);
    }

    /// <summary>Review 3.31.0, A13: "overwrite" stayed after cancel and skipped the check of a later write of the row.</summary>
    [Fact]
    public async Task An_overwrite_chosen_before_a_cancel_does_not_carry_over_to_the_next_write()
    {
        var scope = await _app.OpenAsync(TestApp.Profile("Test"));
        var (workspace, tab) = AddTab(scope);
        tab.Changes!.SetValue(Row(1, "Meier"), 1, "Müller");
        var editor = await EditorAsync(scope);
        editor.FailFlush = operations => editor.LastFlush!.Overwrite?.Contains(operations[0].Change.Id) == true
            ? Locked(operations[0]) // overwritten, but then the row is locked
            : new ConcurrencyConflictException(operations[0], []);

        Assert.False(await _app.Editing.FlushAsync(workspace));
        await _app.Editing.ResolveAsync(ProblemChoice.Overwrite);
        Assert.IsType<LockConflictException>(_app.Editing.Problem?.Error);
        await _app.Editing.ResolveAsync(ProblemChoice.Cancel);

        Assert.False(await _app.Editing.FlushAsync(workspace));
        Assert.True(editor.LastFlush!.Overwrite is null or { Count: 0 });
        Assert.IsType<ConcurrencyConflictException>(_app.Editing.Problem?.Error);
    }

    /// <summary>Review 3.31.0, A14: resolving a redo's problem wrote everything pending of every tab, without the redo.</summary>
    [Fact]
    public async Task Retrying_a_stopped_redo_writes_only_the_redo()
    {
        var scope = await _app.OpenAsync(TestApp.Profile("Test"));
        var (workspace, tab) = AddTab(scope);
        tab.Changes!.SetValue(Row(1, "Meier"), 1, "Müller");
        Assert.True(await _app.Editing.FlushAsync(workspace));
        Assert.True(await _app.Editing.UndoLastAsync(workspace));
        var undone = Assert.Single(_app.Editing.SummaryOf(workspace).Undone);
        var (_, other) = AddTab(scope); // a second tab of the same workspace
        other.Changes!.SetValue(Row(2, "Lang"), 1, "Kurz"); // pending elsewhere, not part of the redo
        var editor = await EditorAsync(scope);
        editor.FailFlush = operations => Locked(operations[0]);

        Assert.False(await _app.Editing.RedoGridAsync(workspace, undone.Id));
        Assert.Equal(undone.Id, _app.Editing.Problem?.RedoOf);
        editor.FailFlush = null;
        await _app.Editing.ResolveAsync(ProblemChoice.Retry);

        Assert.Null(_app.Editing.Problem);
        Assert.Equal(undone.Id, Assert.Single(editor.Actions).RedoOf);
        Assert.Equal((0, 1), (tab.Changes.PendingCount, tab.Changes.FlushedCount));
        Assert.Equal((1, 0), (other.Changes.PendingCount, other.Changes.FlushedCount));
    }

    /// <summary>R4b: closing a workspace from the workspace bar left what the editing kept of it until the connection closed.</summary>
    [Fact]
    public async Task Closing_a_workspace_forgets_its_write_history()
    {
        var scope = await _app.OpenAsync(TestApp.Profile("Test"));
        var (workspace, tab) = AddTab(scope);
        tab.Changes!.SetValue(Row(1, "Meier"), 1, "Müller");
        Assert.True(await _app.Editing.FlushAsync(workspace));
        Assert.True(await _app.Editing.UndoLastAsync(workspace));
        tab.Changes.DiscardPending(); // nothing left to lose, but the write taken back is still known
        Assert.True(_app.Editing.History.Knows(workspace.WorkspaceId));
        await scope.Workspaces.CreateAsync();
        _app.Shell.SyncWorkspaces(scope.Id, scope.Workspaces.Open, scope.Workspaces.Active?.Id, scope.Active.Schema!);

        await _app.Lifecycle.CloseWorkspaceAsync(workspace.WorkspaceId);

        Assert.Null(_app.Lifecycle.PendingLeave);
        Assert.False(scope.Workspaces.Find(workspace.WorkspaceId)!.IsOpen);
        Assert.False(_app.Editing.History.Knows(workspace.WorkspaceId));
    }

    public ValueTask DisposeAsync() => _app.DisposeAsync();
}
