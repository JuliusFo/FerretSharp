using FerretSharp.UI.State;

namespace FerretSharp.UI.Tests.State;

/// <summary>Cancelling on a pool thread (review 3.54.0, A3): the caller does not wait for it, and a freed source is no error.</summary>
public sealed class CancelOffThreadTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_caller_does_not_wait_for_the_cancel_and_a_freed_source_is_no_error()
    {
        using var source = new CancellationTokenSource();
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // A break that blocks (ODP.NET over a silent VPN): run on the caller's thread, Run would never return.
        source.Token.Register(() =>
        {
            entered.TrySetResult();
            release.Wait(Ct);
        });

        CancelOffThread.Run(source);

        await entered.Task.WaitAsync(Ct);
        release.Set();

        var freed = new CancellationTokenSource();
        freed.Dispose();
        CancelOffThread.Run(freed);
        CancelOffThread.Run(null);
    }
}
