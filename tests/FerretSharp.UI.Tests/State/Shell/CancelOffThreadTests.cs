using FerretSharp.UI.State;

namespace FerretSharp.UI.Tests.State;

/// <summary>Cancelling on a pool thread (review 3.54.0, A3): it arrives, and a source its owner freed meanwhile is no error.</summary>
public sealed class CancelOffThreadTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_cancel_arrives_on_another_thread_and_a_freed_source_is_no_error()
    {
        using var source = new CancellationTokenSource();
        var cancelledOn = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        source.Token.Register(() => cancelledOn.TrySetResult(Environment.CurrentManagedThreadId));

        CancelOffThread.Run(source);

        Assert.NotEqual(Environment.CurrentManagedThreadId, await cancelledOn.Task.WaitAsync(Ct));

        var freed = new CancellationTokenSource();
        freed.Dispose();
        CancelOffThread.Run(freed);
        CancelOffThread.Run(null);
    }
}
