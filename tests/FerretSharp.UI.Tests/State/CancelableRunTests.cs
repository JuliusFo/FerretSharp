using FerretSharp.UI.State;

namespace FerretSharp.UI.Tests.State;

/// <summary>
/// 3.41.0: a second click on "Test" while the first test ran started a second run over the same token source field – the
/// first run disposed the second's source, set the field to null, and the second ended in a NullReferenceException.
/// </summary>
public sealed class CancelableRunTests
{
    [Fact]
    public async Task A_second_run_while_one_runs_starts_nothing()
    {
        var run = new CancelableRun();
        var first = new TaskCompletionSource();
        var secondStarted = false;

        var running = run.TryRunAsync(_ => first.Task);
        Assert.True(run.Running);
        Assert.False(await run.TryRunAsync(_ => { secondStarted = true; return Task.CompletedTask; }));

        first.SetResult();
        Assert.True(await running);
        Assert.False(secondStarted);
        Assert.False(run.Running);
    }

    [Fact]
    public async Task Cancel_reaches_the_running_run_and_ends_it()
    {
        var run = new CancelableRun();
        var running = run.TryRunAsync(token => Task.Delay(Timeout.Infinite, token));

        run.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        Assert.False(run.Running);
    }

    [Fact]
    public async Task After_a_run_the_next_one_gets_a_fresh_token()
    {
        var run = new CancelableRun();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.TryRunAsync(token =>
        {
            run.Cancel();
            token.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }));

        var cancelled = true;
        Assert.True(await run.TryRunAsync(token => { cancelled = token.IsCancellationRequested; return Task.CompletedTask; }));
        Assert.False(cancelled);

        run.Cancel(); // nothing running: no ObjectDisposedException from the source of the last run
    }
}
