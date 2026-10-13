namespace FerretSharp.Core.Data;

/// <summary>
/// A query that may take only so long (incoming FK counts, display names): FK columns are often not indexed, and over the
/// VPN a slow count must not hold the grid or the menu (R5b: before, a copy each).
/// </summary>
public static class TimedQuery
{
    /// <summary>
    /// Runs <paramref name="query"/>; <c>default</c> (null) if it took longer than <paramref name="timeout"/>. The statement is
    /// then cancelled on the server; the result returns at the timeout even if the server takes a moment to stop (the
    /// session stays busy until it has). Cancelling <paramref name="cancellationToken"/> throws as usual.
    /// </summary>
    /// <param name="time">The clock of the time limit (tests); the system's if null.</param>
    public static async Task<T?> RunAsync<T>(
        Func<CancellationToken, Task<T>> query, TimeSpan timeout, CancellationToken cancellationToken, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(query);
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var running = query(cts.Token);
        try
        {
            return await running.WaitAsync(timeout, time ?? TimeProvider.System, cancellationToken);
        }
        catch (TimeoutException)
        {
            await cts.CancelAsync();
            return default;
        }
        finally
        {
            // Dispose only once the statement has ended: its cancel registration uses the token. A query that fails after
            // the timeout (connection lost) is observed here, not left as an unobserved task exception.
            _ = running.ContinueWith(finished =>
            {
                _ = finished.Exception;
                cts.Dispose();
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }
}
