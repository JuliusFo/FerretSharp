using FerretSharp.Core.Connections;
using FerretSharp.Core.Resources;
using FerretSharp.Core.Settings;
using FerretSharp.UI.Resources;
using Microsoft.Extensions.Logging;

namespace FerretSharp.UI.State;

public enum ReconnectPhase
{
    /// <summary>A write, commit or export still holds a session of the connection: it starts once that has ended.</summary>
    Holding,

    Connecting,

    /// <summary>The last attempt failed while the network or the database was away; the next one starts at <see cref="ReconnectRun.NextAt"/>.</summary>
    Waiting,
}

/// <summary>A lost connection being connected again by itself (<see cref="AutoReconnect"/>).</summary>
public sealed class ReconnectRun
{
    internal ReconnectRun(DateTimeOffset lostAt, IReadOnlyList<string> unlocked) => (LostAt, Unlocked) = (lostAt, unlocked);

    public DateTimeOffset LostAt { get; }

    public ReconnectPhase Phase { get; internal set; }

    /// <summary>Attempts started so far.</summary>
    public int Attempts { get; internal set; }

    /// <summary>While <see cref="ReconnectPhase.Waiting"/>: when the next attempt starts.</summary>
    public DateTimeOffset? NextAt { get; internal set; }

    /// <summary>Workspaces that were unlocked for writing: connected again, they are locked (ADR 0008) – the notice says so.</summary>
    internal IReadOnlyList<string> Unlocked { get; }

    internal ITimer? Timer { get; set; }
}

/// <summary>
/// Connects a lost connection again by itself, if switched on (<see cref="AppSettings.AutoReconnect"/>, decision of the user
/// 2026-10-21): only when none of its workspaces had uncommitted work – that loss is for the user to see, so the banner
/// stays as before –, where the user is (no switch, no jump), and while the network or the database is away again with
/// pauses (<see cref="ReconnectPolicy"/>). A connection in the background is connected again as well. Runs on the UI thread:
/// <paramref name="post"/> brings the timers' calls there (the shell's <c>Run</c>).
/// </summary>
/// <param name="reconnect">One attempt, ending when connecting has ended (<see cref="WorkspaceLifecycle.ReconnectInPlaceAsync"/>).</param>
public sealed class AutoReconnect(
    ShellState shell,
    ConnectionHub hub,
    WorkspaceEditing editing,
    AppSettingsService settings,
    Func<ConnectionScope, Task> reconnect,
    Action<Func<Task>> post,
    ILogger<AutoReconnect> logger,
    TimeProvider? time = null) : IDisposable
{
    /// <summary>While a write, commit or export holds a session, how often it looks again.</summary>
    public static readonly TimeSpan HoldRecheck = TimeSpan.FromSeconds(2);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Dictionary<ConnectionScope, ReconnectRun> _runs = [];

    public ReconnectRun? RunOf(ConnectionScope scope) => _runs.GetValueOrDefault(scope);

    /// <summary>
    /// The connection is lost and stays so although reconnecting by itself is on: uncommitted work was lost with it (the
    /// banner says why it did not reconnect).
    /// </summary>
    public bool KeptForWork(ConnectionScope scope) =>
        settings.Current.AutoReconnect && scope.Lost is not null && !_runs.ContainsKey(scope) && HasWork(scope);

    /// <summary>Something about the connection changed (the shell calls it for every open connection): a loss starts a run.</summary>
    public void Check(ConnectionScope scope)
    {
        if (!hub.Open.Contains(scope))
        {
            Stop(scope);
            return;
        }

        if (_runs.TryGetValue(scope, out var running))
        {
            if (running is { Phase: ReconnectPhase.Holding } && scope.Lost is null)
            {
                Stop(scope); // "Reconnect" in the banner meanwhile
            }

            return;
        }

        if (!settings.Current.AutoReconnect || scope.Lost is not { } lost || !scope.Active.IsConnected || HasWork(scope))
        {
            return;
        }

        var unlocked = scope.Workspaces.Open.Where(w => scope.Workspaces.IsUnlocked(w.Id)).Select(w => w.Name).ToList();
        var run = new ReconnectRun(_time.GetUtcNow(), unlocked);
        _runs[scope] = run;
        logger.LogInformation("{Connection}: lost ({Error}), reconnecting by itself", NameOf(scope), lost.Display);
        post(() => AttemptAsync(scope, run));
    }

    /// <summary>"Try now" while waiting for the next attempt.</summary>
    public void TryNow(ConnectionScope scope)
    {
        if (RunOf(scope) is { Phase: ReconnectPhase.Waiting } run)
        {
            run.Timer?.Dispose();
            post(() => AttemptAsync(scope, run));
        }
    }

    /// <summary>No more attempts ("Stop", or the connection was closed); a failed connection stays failed.</summary>
    public void Stop(ConnectionScope scope)
    {
        if (_runs.Remove(scope, out var run))
        {
            run.Timer?.Dispose();
            shell.NotifyChanged();
        }
    }

    private async Task AttemptAsync(ConnectionScope scope, ReconnectRun run)
    {
        if (!IsCurrent(scope, run))
        {
            return;
        }

        // Before the first attempt the loss is still there; later ones follow a failed attempt. Anything else means the user
        // connected, reconnected or disconnected meanwhile – or switched it off.
        var expected = run.Attempts == 0 ? scope.Lost is not null && scope.Active.IsConnected : scope.Active.Status == ConnectionStatus.Failed;
        if (!expected || !settings.Current.AutoReconnect)
        {
            Stop(scope);
            return;
        }

        if (run.Attempts == 0 && Busy(scope))
        {
            run.Phase = ReconnectPhase.Holding;
            Schedule(scope, run, HoldRecheck);
            shell.NotifyChanged();
            return;
        }

        run.Phase = ReconnectPhase.Connecting;
        run.NextAt = null;
        run.Attempts++;
        shell.NotifyChanged();
        logger.LogInformation("{Connection}: reconnect attempt {Attempt}", NameOf(scope), run.Attempts);
        await reconnect(scope);
        if (!IsCurrent(scope, run))
        {
            return;
        }

        var now = _time.GetUtcNow();
        if (scope.Active.IsConnected)
        {
            _runs.Remove(scope);
            logger.LogInformation("{Connection}: connected again after {Attempts} attempt(s)", NameOf(scope), run.Attempts);
            shell.Notify(
                TextFormat.Format(ShellText.AutoReconnect_Done, NameOf(scope)),
                run.Unlocked.Count > 0 ? [TextFormat.Format(ShellText.AutoReconnect_LockedAgain, string.Join(", ", run.Unlocked))] : null);
            return;
        }

        var error = scope.Active.Error;
        if (scope.Active.Status == ConnectionStatus.Failed && ReconnectPolicy.IsWorthRetrying(error)
            && ReconnectPolicy.PauseAfter(run.Attempts, now - run.LostAt) is { } pause)
        {
            run.Phase = ReconnectPhase.Waiting;
            run.NextAt = now + pause;
            Schedule(scope, run, pause);
            shell.NotifyChanged();
            return;
        }

        // Refused (password, account), given up after a while, or cancelled: what the connection shows stays as it is.
        _runs.Remove(scope);
        if (scope.Active.Status == ConnectionStatus.Failed)
        {
            logger.LogWarning("{Connection}: not reconnected after {Attempts} attempt(s): {Error}", NameOf(scope), run.Attempts, error?.Message);
            shell.Notify(TextFormat.Format(ShellText.AutoReconnect_GaveUp, NameOf(scope)), error is null ? null : [error.Message]);
        }
        else
        {
            shell.NotifyChanged();
        }
    }

    private void Schedule(ConnectionScope scope, ReconnectRun run, TimeSpan after)
    {
        run.Timer?.Dispose();
        run.Timer = _time.CreateTimer(_ => post(() => AttemptAsync(scope, run)), null, after, Timeout.InfiniteTimeSpan);
    }

    private bool IsCurrent(ConnectionScope scope, ReconnectRun run) =>
        _runs.TryGetValue(scope, out var current) && current == run && hub.Open.Contains(scope);

    private IEnumerable<WorkspaceTabs> WorkspacesOf(ConnectionScope scope) => shell.AllWorkspaces.Where(w => w.ConnectionId == scope.Id);

    private bool HasWork(ConnectionScope scope) => WorkspacesOf(scope).Any(w => editing.SummaryOf(w).HasWork);

    /// <summary>Reconnecting closes every session of the connection: a write, commit or export on one that still works would end.</summary>
    private bool Busy(ConnectionScope scope) =>
        WorkspacesOf(scope).Any(w => editing.HoldsSession(w.WorkspaceId) || shell.BackgroundReads.ReadingIn(w.WorkspaceId) is not null);

    private static string? NameOf(ConnectionScope scope) => scope.Profile?.Name;

    public void Dispose()
    {
        foreach (var run in _runs.Values)
        {
            run.Timer?.Dispose();
        }

        _runs.Clear();
    }
}
