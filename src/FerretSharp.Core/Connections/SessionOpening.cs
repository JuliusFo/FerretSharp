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
/// When to stop: an error that is not the network's (<see cref="ReconnectPolicy.IsNetworkError"/>) ends the open at once,
/// from any attempt – a refused login answered late (Oracle delays it after failed logins) must not be tried again, each one
/// counts towards <c>FAILED_LOGIN_ATTEMPTS</c> (review of the fix). A network error of the latest attempt ends it once no
/// earlier attempt is running any more (no listener – another attempt within the limit would not fix it; a slow earlier one
/// may still open). A network error of an earlier attempt is the hang ending; it is kept for the message if no attempt opens.
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
        var taken = false;
        try
        {
            for (var attempt = 1; ; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested(); // cancelled just as the limit ran: no further login
                if (race.Winner.IsCompleted)
                {
                    // An earlier attempt ended just as the limit ran: no further login either.
                    var ended = await race.Winner;
                    taken = true;
                    return ended;
                }

                if (attempt > 1)
                {
                    retrying?.Invoke(attempt);
                }

                var number = attempt;
                race.Start(number, Task.Run(() => open(number, cancellationToken), cancellationToken)); // not started once cancelled
                TimeSpan? onlyEarlier = null;
                try
                {
                    var session = await race.Winner.WaitAsync(AttemptLimits[attempt - 1], time, cancellationToken);
                    taken = true;
                    return session;
                }
                catch (TimeoutException) when (attempt < AttemptLimits.Count)
                {
                    if (race.PastLimit())
                    {
                        onlyEarlier = AttemptLimits.Skip(attempt).Aggregate(TimeSpan.Zero, (sum, limit) => sum + limit);
                    }
                }
                catch (TimeoutException)
                {
                    throw new SessionOpenTimeoutException(AttemptLimits, race.LastNetworkError);
                }

                if (onlyEarlier is { } rest)
                {
                    // The latest attempt failed on the network while earlier ones still run (third review): no further
                    // attempt after an error – only the earlier ones, for the time the remaining limits would have taken.
                    try
                    {
                        var session = await race.Winner.WaitAsync(rest, time, cancellationToken);
                        taken = true;
                        return session;
                    }
                    catch (TimeoutException)
                    {
                        throw new SessionOpenTimeoutException(AttemptLimits, race.LastNetworkError);
                    }
                }
            }
        }
        finally
        {
            race.End(taken);
        }
    }

    /// <summary>The attempts of one open: the first session wins, sessions after the end are closed.</summary>
    internal sealed class Race
    {
        private readonly TaskCompletionSource<IDatabaseConnection> _winner = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Lock _lock = new();
        private int _latest;
        private int _running;
        private Exception? _latestNetworkError;
        private volatile string? _lastNetworkError;

        public Task<IDatabaseConnection> Winner => _winner.Task;

        public string? LastNetworkError => _lastNetworkError;

        public void Start(int attempt, Task<IDatabaseConnection> opening)
        {
            lock (_lock)
            {
                _latest = attempt;
                _latestNetworkError = null;
                _running++;
            }

            // As it ends: a late network error is known before a limit that runs right after it.
            _ = opening.ContinueWith(opened => EndedAsync(attempt, opened), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default)
                .Unwrap();
        }

        /// <summary>The latest attempt's limit ran: until the next one starts, none is the latest.</summary>
        /// <returns>The latest attempt had already failed on the network (earlier ones still run): no next one may start.</returns>
        public bool PastLimit()
        {
            lock (_lock)
            {
                _latest = 0;
                return _latestNetworkError is not null;
            }
        }

        /// <summary>
        /// No session is taken any more: one that opens from now on is closed. One that won while the open ended without
        /// taking it (the limit ran or it was cancelled in the same moment) is closed here; a failure there is observed.
        /// </summary>
        /// <param name="taken">The open returned the winner.</param>
        public void End(bool taken)
        {
            if (_winner.TrySetCanceled() || taken)
            {
                return;
            }

            if (Winner.IsCompletedSuccessfully)
            {
                _ = CloseAsync(Winner.Result);
            }
            else
            {
                _ = Winner.Exception;
            }
        }

        private async Task EndedAsync(int attempt, Task<IDatabaseConnection> opened)
        {
            if (opened.IsCompletedSuccessfully)
            {
                lock (_lock)
                {
                    _running--;
                }

                if (!_winner.TrySetResult(opened.Result))
                {
                    await CloseAsync(opened.Result);
                }

                return;
            }

            var error = opened.IsCanceled ? null : opened.Exception!.GetBaseException();
            lock (_lock)
            {
                var stillRunning = --_running > 0;
                if (error is null)
                {
                    if (attempt == _latest)
                    {
                        _winner.TrySetCanceled();
                    }

                    return;
                }

                if (error is not DatabaseException { ErrorCode: var code } || !ReconnectPolicy.IsNetworkError(code))
                {
                    _winner.TrySetException(error); // a refused login ends at once, also a late one (review of the fix)
                    return;
                }

                // A network error: the open ends with the latest attempt's once no attempt is running any more; an earlier
                // attempt may still open (review of the fix: a slow one opened while the latest had already failed).
                _lastNetworkError = error.Message;
                if (attempt == _latest)
                {
                    _latestNetworkError = error;
                }

                if (!stillRunning && _latestNetworkError is { } latest)
                {
                    _winner.TrySetException(latest);
                }
            }
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
