using FerretSharp.Core.Resources;

namespace FerretSharp.Core.Connections;

/// <summary>Every attempt to open the session ran past its limit (<see cref="SessionOpening.AttemptLimits"/>).</summary>
public sealed class SessionOpenTimeoutException(IReadOnlyList<TimeSpan> limits)
    : DatabaseException(TextFormat.Format(ConnectionText.OpenTimedOut, limits.Count, (int)limits.Sum(l => l.TotalSeconds)));

/// <summary>
/// Opens a session in attempts with growing limits. Over the customer VPN, opening sometimes hangs for about 20 s and ends
/// with ORA-12570 ("error reading packet"), while an attempt right after opens in under a second (log of 2026-10-21 to
/// 10-28; a healthy open took 0.4 to 2.4 s). ODP.NET's own <c>ConnectionTimeout</c> (15 s) does not cover that hang.
/// So an attempt past its limit is given up and the next one started; the limits grow, so a slow but healthy line still gets
/// through. A failure within the limit (wrong password, no listener) ends at once: another attempt would not fix it.
/// </summary>
/// <remarks>
/// ODP.NET does not stop opening when cancelled: the TCP connect to an unreachable address runs to its timeout, and Cancel did
/// nothing until then. Giving up returns at once; a session that opens after all is closed right away.
/// </remarks>
public static class SessionOpening
{
    /// <summary>The limit of each attempt, in order; 34 s in all before the open fails (<see cref="SessionOpenTimeoutException"/>).</summary>
    public static readonly IReadOnlyList<TimeSpan> AttemptLimits =
        [TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(15)];

    /// <param name="open">One attempt; its token is cancelled when the attempt is given up.</param>
    /// <param name="retrying">Called before attempt 2, 3 … with its number (1-based).</param>
    /// <exception cref="SessionOpenTimeoutException">The last attempt ran past its limit as well.</exception>
    public static async Task<IDatabaseConnection> OpenAsync(
        Func<CancellationToken, Task<IDatabaseConnection>> open, TimeProvider time, Action<int>? retrying, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            using var expired = new CancellationTokenSource(AttemptLimits[attempt - 1], time);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, expired.Token);
            try
            {
                return await AttemptAsync(open, limit.Token);
            }
            catch (OperationCanceledException) when (expired.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                if (attempt == AttemptLimits.Count)
                {
                    throw new SessionOpenTimeoutException(AttemptLimits);
                }
            }

            retrying?.Invoke(attempt + 1);
        }
    }

    private static async Task<IDatabaseConnection> AttemptAsync(Func<CancellationToken, Task<IDatabaseConnection>> open, CancellationToken cancellationToken)
    {
        var opening = open(cancellationToken);
        try
        {
            return await opening.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            _ = opening.ContinueWith(async opened =>
            {
                if (opened.IsCompletedSuccessfully)
                {
                    await opened.Result.DisposeAsync();
                }
                else
                {
                    _ = opened.Exception; // observed: a failure nobody waits for any more
                }
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
            throw;
        }
    }
}
