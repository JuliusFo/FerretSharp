using FerretSharp.Core.Connections;
using FerretSharp.Core.Data;
using FerretSharp.Core.Workspaces;
using Microsoft.Extensions.Logging;

namespace FerretSharp.UI.State;

/// <summary>
/// Looks at what a workspace's session is doing while one of its statements runs (WP-52, ADR 0027): after
/// <see cref="Delay"/>, one look every <see cref="Interval"/> until the statement ends (<see cref="Dispose"/>). A look goes to
/// the explorer session – the workspace's own is busy – and the next one starts only after the last answered, so a slow
/// network never piles them up. Without rights on the V$ views, or after a failed look, it stops for this statement and says
/// so (<see cref="Unavailable"/>).
/// </summary>
/// <param name="look">One look: <see cref="SessionActivity.None"/> if there is nothing to say, null if it is not possible.</param>
/// <param name="changed">After each look (on any thread: the caller re-renders through InvokeAsync).</param>
/// <param name="time">The clock of the delays (tests); the system's if null.</param>
public sealed class SessionWatch(Func<CancellationToken, Task<SessionActivity?>> look, Action changed, TimeProvider? time = null) : IDisposable
{
    /// <summary>Statements that end sooner cost no extra round trip.</summary>
    public static readonly TimeSpan Delay = TimeSpan.FromSeconds(3);

    /// <summary>Between the end of one look and the start of the next.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);

    private readonly CancellationTokenSource _stop = new();
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private volatile SessionActivity? _latest;
    private volatile bool _unavailable;
    private int _disposed;

    /// <summary>What the last look found; null before the first one.</summary>
    public SessionActivity? Latest => _latest;

    /// <summary>Looking is not possible (no rights on the V$ views, or a look failed): no more looks for this statement.</summary>
    public bool Unavailable => _unavailable;

    /// <summary>The looks of a workspace's statement: its session by tag, through the connection's explorer session.</summary>
    public static SessionWatch For(
        ShellState shell, ILogger logger, ActiveConnection active, WorkspaceManager workspaces, Guid workspaceId, Action changed) =>
        new(async token =>
        {
            // Both appear with the session: while it opens there is nothing to look at yet.
            if (workspaces.SessionTagOf(workspaceId) is not { } tag || active.Schema is not { } schema)
            {
                return SessionActivity.None;
            }

            var knownUnavailable = schema.SessionActivityUnavailable;
            var result = await shell.RunDbAsync(logger, active, () => schema.GetSessionActivityAsync(tag, token), token);
            if (result.Cancelled)
            {
                // A break on the explorer session; with the session's history in a lost connection's log entry, this says who sent it.
                logger.LogInformation("{Connection}: the statement ended while a look at its session ran, the look was cancelled", active.Profile?.Name);
            }

            token.ThrowIfCancellationRequested();
            if (result is { Succeeded: true, Value: null } && !knownUnavailable)
            {
                logger.LogInformation("{Connection}: no read access to V$SESSION/V$SESSION_LONGOPS, running statements show no waits or progress",
                    active.Profile?.Name);
            }

            return result.Succeeded ? result.Value : null;
        }, changed);

    /// <summary>Starts looking; the task ends with <see cref="Dispose"/> or when looking is not possible.</summary>
    public async Task RunAsync()
    {
        CancellationToken token;
        try
        {
            token = _stop.Token;
        }
        catch (ObjectDisposedException)
        {
            return; // the statement ended before looking started
        }

        try
        {
            await Task.Delay(Delay, _time, token);
            while (true)
            {
                var activity = await look(token);
                if (activity is null)
                {
                    _unavailable = true;
                    changed();
                    return;
                }

                _latest = activity;
                changed();
                await Task.Delay(Interval, _time, token);
            }
        }
        catch (OperationCanceledException)
        {
            // the statement ended
        }
    }

    /// <summary>The statement ended: no more looks, a running one is cancelled. May be called more than once.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _stop.Cancel();
            _stop.Dispose();
        }
    }
}
