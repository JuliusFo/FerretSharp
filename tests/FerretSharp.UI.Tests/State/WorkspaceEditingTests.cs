using FerretSharp.Core.Connections;
using FerretSharp.Core.Data;
using FerretSharp.Core.Query;

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
        var held = new TaskCompletionSource();
        _app.OnCommit = () => held.Task;

        var slow = _app.Editing.CommitAsync(_app.WorkspaceOf(test));

        Assert.True(_app.Editing.IsBusy(test.Workspaces.Active!.Id));
        Assert.False(_app.Editing.IsBusy(dev.Workspaces.Active!.Id));

        _app.OnCommit = null;
        Assert.True(await _app.Editing.CommitAsync(_app.WorkspaceOf(dev)));

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

    /// <summary>R4a: on Prod, a statement of another tab finishing after the confirmation opened was committed unseen.</summary>
    [Fact]
    public async Task Commit_after_a_confirmation_refuses_writes_it_did_not_show()
    {
        var scope = await _app.OpenAsync(TestApp.Profile("Prod", ConnectionKind.Prod));
        var workspace = _app.WorkspaceOf(scope);
        var id = scope.Workspaces.Active!.Id;
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

    public ValueTask DisposeAsync() => _app.DisposeAsync();
}
