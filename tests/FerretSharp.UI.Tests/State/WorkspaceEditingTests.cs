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

    public ValueTask DisposeAsync() => _app.DisposeAsync();
}
