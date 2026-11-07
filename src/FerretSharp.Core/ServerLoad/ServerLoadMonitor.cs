namespace FerretSharp.Core.ServerLoad;

/// <summary>What a look is doing, for the page while it waits.</summary>
public enum ServerLoadStep
{
    /// <summary>Reading the figures of every cursor once (thousands of rows, a few seconds over a VPN).</summary>
    Baseline,

    /// <summary>Reading every cursor again, as every hour (<see cref="ServerLoadTracker.FullReadDue"/>): nothing seen is lost.</summary>
    FullRead,
}

/// <summary>
/// One session's looks at the server (WP-75): the counters, then the cursors active since the look before (all of them for
/// the baseline), then what is missing of their text, module and schema, then the active sessions – into a
/// <see cref="ServerLoadTracker"/> that outlives the session.
/// </summary>
public sealed class ServerLoadMonitor(IServerLoadReader reader, ServerLoadTracker tracker)
{
    /// <summary>Statements per query for text, module and schema (one bind each).</summary>
    public const int InfoBatch = 100;

    /// <summary>Statements asked for per look: after a long gap thousands may be new; the most expensive come first, the rest next time.</summary>
    public const int InfoPerLook = 500;

    public ServerLoadTracker Tracker => tracker;

    /// <summary>Read once by <see cref="OpenAsync"/>.</summary>
    public ServerEnvironment? Environment { get; private set; }

    /// <summary>The active sessions of the latest look.</summary>
    public IReadOnlyList<ActiveSession> Sessions { get; private set; } = [];

    /// <summary>The first look of a new session, after checking the rights and reading how the server is set up.</summary>
    /// <exception cref="ServerLoadUnavailableException">The user may not read the V$ views.</exception>
    public async Task OpenAsync(Action<ServerLoadStep>? step, CancellationToken cancellationToken)
    {
        Environment = await reader.ReadEnvironmentAsync(cancellationToken);
        await LookAsync(step, cancellationToken);
    }

    /// <returns>The load since the look before; null for the first look and after a restart of the instance.</returns>
    public async Task<LoadSample?> LookAsync(Action<ServerLoadStep>? step, CancellationToken cancellationToken)
    {
        var full = tracker.FullReadDue; // decided before the look, as the page's time limit is (ServerLoadService)
        var load = await reader.ReadCountersAsync(cancellationToken);
        var sample = tracker.AddLoad(load);
        if (tracker.NeedsBaseline)
        {
            step?.Invoke(ServerLoadStep.Baseline);
            tracker.Rebase(load, await reader.ReadStatementsAsync(null, cancellationToken));
        }
        else if (full)
        {
            step?.Invoke(ServerLoadStep.FullRead);
            tracker.AddStatements(await reader.ReadStatementsAsync(null, cancellationToken), complete: true);
        }
        else
        {
            var changed = await reader.ReadStatementsAsync(ActiveSince(tracker.StatementsSince!.Value), cancellationToken);
            tracker.AddStatements(changed);
        }

        var missing = tracker.SqlIdsWithoutInfo(InfoPerLook);
        foreach (var batch in missing.Chunk(InfoBatch))
        {
            tracker.AddInfo(batch, await reader.ReadStatementInfoAsync(batch, cancellationToken));
        }

        Sessions = await reader.ReadActiveSessionsAsync(cancellationToken);
        return sample;
    }

    /// <summary>
    /// <c>LAST_ACTIVE_TIME</c> is a DATE (whole seconds): the second of the look before and the one before it count again, so
    /// nothing active in the same second as that look is missed. A cursor read twice adds nothing the second time.
    /// </summary>
    public static DateTime ActiveSince(DateTime statementsAt) =>
        new DateTime(statementsAt.Ticks - statementsAt.Ticks % TimeSpan.TicksPerSecond, statementsAt.Kind).AddSeconds(-1);
}
