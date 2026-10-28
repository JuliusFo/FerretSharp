using FerretSharp.Core.Resources;

namespace FerretSharp.Core.Connections;

/// <summary>No attempt to open the session opened one within the limits (<see cref="SessionOpening.AttemptLimits"/>).</summary>
/// <param name="lastError">The last network error of an attempt given up on, if one came meanwhile: the actual cause (VPN not up).</param>
public sealed class SessionOpenTimeoutException(IReadOnlyList<TimeSpan> limits, string? lastError)
    : DatabaseException(lastError is null
        ? TextFormat.Format(ConnectionText.OpenTimedOut, limits.Count, Seconds(limits))
        : TextFormat.Format(ConnectionText.OpenTimedOutWithError, limits.Count, Seconds(limits), lastError))
{
    private static int Seconds(IReadOnlyList<TimeSpan> limits) => (int)limits.Sum(l => l.TotalSeconds);
}

/// <summary>
/// Opens a session in attempts with growing limits. Over the customer VPN, opening sometimes hangs for about 20 s and ends
/// with ORA-12570 ("error reading packet"), while an attempt right after opens in under a second (log of 2026-10-21 to
/// 10-28; a healthy open took 0.4 to 2.4 s). ODP.NET's own <c>ConnectionTimeout</c> (15 s) does not cover that hang.
/// So past its limit the next attempt starts – the earlier ones keep running, and the first session to open wins (a slow but
/// healthy line is not thrown away); the others are closed when they open.
/// </summary>
/// <remarks>
/// <para>
/// When to stop: the latest attempt failing ends the open with its error (wrong password, no listener – another attempt would
/// not fix it). An earlier attempt failing ends it only with an error that is not the network's
/// (<see cref="ReconnectPolicy.IsNetworkError"/>): a refused login answered late (Oracle delays it after failed logins) must
/// not be tried again, each one counts towards <c>FAILED_LOGIN_ATTEMPTS</c> (review of the fix). A late network error is the
/// hang ending; it is kept for the message if no attempt opens.
/// </para>
/// <para>
/// ODP.NET does not stop opening when cancelled: the TCP connect to an unreachable address runs to its timeout, and Cancel did
/// nothing until then. Cancelling returns at once. Each attempt starts on the thread pool, so a driver that blocks before
/// returning its task cannot hold up the limit.
/// </para>
/// </remarks>
public static class SessionOpening
{
    /// <summary>The limit of each attempt, in order; 43 s in all before the open fails (<see cref="SessionOpenTimeoutException"/>).</summary>
    public static readonly IReadOnlyList<TimeSpan> AttemptLimits =
        [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(15)];

    /// <param name="open">One attempt with its number (1-based) and the caller's token.</param>
    /// <param name="retrying">Called before attempt 2, 3 … with its number.</param>
    /// <exception cref="SessionOpenTimeoutException">No attempt opened a session within the limits.</exception>
    public static async Task<IDatabaseConnection> OpenAsync(
        Func<int, CancellationToken, Task<IDatabaseConnection>> open, TimeProvider time, Action<int>? retrying, CancellationToken cancellationToken)
    {
        var race = new Race();
        try
        {
            for (var attempt = 1; ; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested(); // cancelled just as the limit ran: no further login
                if (race.Winner.IsCompleted)
                {
                    return await race.Winner; // an earlier attempt ended just as the limit ran: no further login either
                }

                if (attempt > 1)
                {
                    retrying?.Invoke(attempt);
                }

                var number = attempt;
                race.Start(number, Task.Run(() => open(number, cancellationToken), cancellationToken)); // not started once cancelled
                try
                {
                    return await race.Winner.WaitAsync(AttemptLimits[attempt - 1], time, cancellationToken);
                }
                catch (TimeoutException) when (attempt < AttemptLimits.Count)
                {
                    race.PastLimit();
                }
                catch (TimeoutException)
                {
                    throw new SessionOpenTimeoutException(AttemptLimits, race.LastNetworkError);
                }
            }
        }
        finally
        {
            race.End();
        }
    }

    /// <summary>The attempts of one open: the first session wins, sessions after the end are closed.</summary>
    private sealed class Race
    {
        private readonly TaskCompletionSource<IDatabaseConnection> _winner = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private volatile int _latest;
        private volatile string? _lastNetworkError;

        public Task<IDatabaseConnection> Winner => _winner.Task;

        public string? LastNetworkError => _lastNetworkError;

        public void Start(int attempt, Task<IDatabaseConnection> opening)
        {
            _latest = attempt;
            // As it ends: a late network error is known before a limit that runs right after it.
            _ = opening.ContinueWith(opened => EndedAsync(attempt, opened), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default)
                .Unwrap();
        }

        /// <summary>The latest attempt ran past its limit: until the next one starts, none is the latest.</summary>
        public void PastLimit() => _latest = 0;

        /// <summary>No session is taken any more: one that opens from now on is closed.</summary>
        public void End() => _winner.TrySetCanceled();

        private async Task EndedAsync(int attempt, Task<IDatabaseConnection> opened)
        {
            if (opened.IsCompletedSuccessfully)
            {
                if (!_winner.TrySetResult(opened.Result))
                {
                    await CloseAsync(opened.Result);
                }

                return;
            }

            if (opened.IsCanceled)
            {
                if (attempt == _latest)
                {
                    _winner.TrySetCanceled();
                }

                return;
            }

            var error = opened.Exception!.GetBaseException();
            if (attempt != _latest && error is DatabaseException { ErrorCode: var code } && ReconnectPolicy.IsNetworkError(code))
            {
                _lastNetworkError = error.Message;
                return;
            }

            _winner.TrySetException(error);
        }

        private static async Task CloseAsync(IDatabaseConnection connection)
        {
            try
            {
                await connection.DisposeAsync();
            }
            catch (Exception ex) when (ex is DatabaseException or OperationCanceledException or ObjectDisposedException or InvalidOperationException)
            {
                // A session nobody uses; closing it is all that is left to try.
            }
        }
    }
}
