using System.Collections.Concurrent;

namespace FerretSharp.Testing;

/// <summary>
/// A stand-in for the app's UI thread: one thread whose <see cref="SynchronizationContext"/> runs everything posted to it,
/// as the BlazorWebView's dispatcher does. Without it a test cannot see work that stays on the caller's thread – the
/// test runner has no context, so <c>await Task.Yield()</c> already lands on the thread pool there.
/// </summary>
internal sealed class UiThread : IDisposable
{
    private readonly Context _context = new();
    private readonly Thread _thread;

    public UiThread()
    {
        _thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(_context);
            foreach (var (callback, state) in _context.Queue.GetConsumingEnumerable())
            {
                callback(state);
            }
        }) { IsBackground = true, Name = "UI stand-in" };
        _thread.Start();
    }

    public int ThreadId => _thread.ManagedThreadId;

    /// <summary>Whether the calling code runs on this thread.</summary>
    public bool IsCurrent => Environment.CurrentManagedThreadId == ThreadId;

    /// <summary>Starts <paramref name="work"/> on this thread, as a click handler would.</summary>
    public Task<T> RunAsync<T>(Func<Task<T>> work)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _context.Post(async _ =>
        {
            try
            {
                done.SetResult(await work());
            }
            catch (Exception ex)
            {
                done.SetException(ex);
            }
        }, null);
        return done.Task;
    }

    public Task RunAsync(Func<Task> work) => RunAsync(async () =>
    {
        await work();
        return true;
    });

    public void Dispose() => _context.Queue.CompleteAdding();

    private sealed class Context : SynchronizationContext
    {
        public BlockingCollection<(SendOrPostCallback Callback, object? State)> Queue { get; } = [];

        public override void Post(SendOrPostCallback d, object? state) => Queue.Add((d, state));

        public override SynchronizationContext CreateCopy() => this;
    }
}
