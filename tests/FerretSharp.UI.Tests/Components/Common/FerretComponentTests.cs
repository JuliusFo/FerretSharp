using FerretSharp.UI.Components;

namespace FerretSharp.UI.Tests.Components.Common;

/// <summary>
/// Cancellation in guarded work stays silent: thrown on, the WPF dispatcher of the BlazorWebView rethrew it from <c>Fire</c> as
/// an unhandled exception on the UI thread – an "Unexpected error" dialog after a cancelled grid load.
/// </summary>
public sealed class FerretComponentTests
{
    private sealed class Guarded : FerretComponent
    {
        public Task Run(Func<Task> work) => GuardAsync(work);

        public Task<int> Run(Func<Task<int>> work, int failed) => GuardAsync(work, failed);
    }

    [Fact]
    public async Task Cancelled_work_ends_quietly()
    {
        var guarded = new Guarded();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await guarded.Run(() => Task.Delay(Timeout.Infinite, cts.Token)); // TaskCanceledException
        await guarded.Run(() => throw new OperationCanceledException());
    }

    [Fact]
    public async Task Cancelled_work_with_a_result_gives_the_failure_value()
    {
        var guarded = new Guarded();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        Assert.Equal(-1, await guarded.Run(async () =>
        {
            await Task.Delay(Timeout.Infinite, cts.Token);
            return 1;
        }, failed: -1));
        Assert.Equal(2, await guarded.Run(() => Task.FromResult(2), failed: -1));
    }
}
