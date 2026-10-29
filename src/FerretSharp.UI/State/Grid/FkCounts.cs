using System.Diagnostics;
using System.Globalization;
using FerretSharp.Core.Connections;
using FerretSharp.Core.Query;
using FerretSharp.Core.Resources;
using FerretSharp.Core.Schema;
using FerretSharp.Core.Workspaces;
using FerretSharp.UI.Resources;
using Microsoft.Extensions.Logging;

namespace FerretSharp.UI.State;

/// <summary>Where the count of an incoming FK jump stands.</summary>
public abstract record FkCount
{
    public sealed record Loading : FkCount;

    public sealed record Done(long Count) : FkCount;

    /// <summary>Gave up after <see cref="FkNavigation.CountTimeout"/> (FK columns are often not indexed).</summary>
    public sealed record TimedOut : FkCount;

    public sealed record Failed(string Message) : FkCount;
}

/// <summary>
/// Counts the rows behind incoming FK jumps on the workspace's session (the context menu of the grid and the form, WP-21),
/// one at a time: the session runs one statement after the other, and a jump waits until the running count has ended
/// (a cancelled one too, until the server confirmed it). <see cref="FkCountMode"/> decides whether all are counted at once
/// (<see cref="CountAllAsync"/>) or only the one the mouse rests on (<see cref="HoverAsync"/>). Cancelling – the menu closes,
/// the form moves to another row or jumps – stops the rest.
/// </summary>
/// <param name="count">One count; <see cref="For"/> builds it for a workspace.</param>
/// <param name="changed">After each change of a state (on any thread: the caller re-renders through InvokeAsync).</param>
/// <param name="time">The clock of <see cref="HoverDelay"/> (tests); the system's if null.</param>
public sealed class FkCounts(Func<FkJump, CancellationToken, Task<DbResult<long?>>> count, Action changed, TimeProvider? time = null)
{
    /// <summary>How long the mouse rests on an entry before it is counted: passing over entries counts nothing.</summary>
    public static readonly TimeSpan HoverDelay = TimeSpan.FromMilliseconds(400);

    private readonly Lock _lock = new();
    private readonly Dictionary<FkJump, FkCount> _states = [];
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private FkJump? _hovered;
    private bool _running;

    /// <summary>Counts on the workspace's session, the target's structure through the schema cache; logs each with its duration.</summary>
    public static FkCounts For(
        ShellState shell, ILogger logger, ActiveConnection active, WorkspaceManager workspaces, Guid workspaceId, Action changed) =>
        new((jump, token) => shell.RunDbAsync(logger, active, async () =>
        {
            var schema = active.Schema ?? throw new OperationCanceledException();
            var started = Stopwatch.GetTimestamp();
            var target = await schema.GetDetailsAsync(FkTargets.SummaryOf(active, jump.Table), token);
            var data = await workspaces.GetDataAsync(workspaceId, token);
            var rows = await FkNavigation.CountAsync(data, target, jump, FkNavigation.CountTimeout, token);
            // Waiting for the session included: a slow jump after the menu shows here (no values: names only).
            logger.LogDebug("FK count {ForeignKey} on {Table}: {Rows} after {Elapsed} ms",
                jump.ForeignKey.Name, jump.Table, rows?.ToString(CultureInfo.InvariantCulture) ?? "timeout", (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            return rows;
        }, token), changed);

    public FkCount? Of(FkJump jump)
    {
        lock (_lock)
        {
            return _states.GetValueOrDefault(jump);
        }
    }

    /// <summary>Counts every available jump, one after the other (<see cref="FkCountMode.Immediately"/>).</summary>
    public async Task CountAllAsync(IReadOnlyList<FkJump> jumps, CancellationToken token)
    {
        List<FkJump> pending;
        lock (_lock)
        {
            _states.Clear();
            _hovered = null;
            pending = jumps.Where(j => j.IsAvailable).ToList();
            foreach (var jump in pending)
            {
                _states[jump] = new FkCount.Loading();
            }
        }

        foreach (var jump in pending)
        {
            if (!await CountOneAsync(jump, token))
            {
                return;
            }
        }
    }

    /// <summary>Forgets the counts of other jumps (the form moved to another row); nothing is counted yet.</summary>
    public void Reset()
    {
        lock (_lock)
        {
            _states.Clear();
            _hovered = null;
        }
    }

    /// <summary>
    /// The mouse entered <paramref name="jump"/> (<see cref="FkCountMode.OnHover"/>): once it rested there for
    /// <see cref="HoverDelay"/>, counts the jump under the mouse – unless it is counted already or a count is running; that
    /// one counts the jump under the mouse when it ends.
    /// </summary>
    public async Task HoverAsync(FkJump jump, CancellationToken token)
    {
        lock (_lock)
        {
            _hovered = jump;
        }

        try
        {
            await Task.Delay(HoverDelay, _time, token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (true)
        {
            FkJump next;
            lock (_lock)
            {
                if (_running || _hovered is not { IsAvailable: true } hovered || _states.ContainsKey(hovered))
                {
                    return;
                }

                next = hovered;
                _running = true;
                _states[next] = new FkCount.Loading();
            }

            changed();
            bool counted;
            try
            {
                counted = await CountOneAsync(next, token);
            }
            finally
            {
                lock (_lock)
                {
                    _running = false;
                }
            }

            if (!counted)
            {
                return;
            }
        }
    }

    /// <summary>The mouse left <paramref name="jump"/>: a count waiting for the delay or a running one does not take it up.</summary>
    public void Leave(FkJump jump)
    {
        lock (_lock)
        {
            if (Equals(_hovered, jump))
            {
                _hovered = null;
            }
        }
    }

    /// <returns>False if cancelled: nothing more to count.</returns>
    private async Task<bool> CountOneAsync(FkJump jump, CancellationToken token)
    {
        var result = await count(jump, token);
        if (result.Cancelled || token.IsCancellationRequested)
        {
            // No spinner stays behind (the form after a jump, when the user comes back): on hover it counts again.
            lock (_lock)
            {
                foreach (var loading in _states.Where(s => s.Value is FkCount.Loading).Select(s => s.Key).ToList())
                {
                    _states.Remove(loading);
                }
            }

            changed();
            return false;
        }

        lock (_lock)
        {
            _states[jump] = result.Error is { } error ? new FkCount.Failed(error.Display)
                : result.Value is { } n ? new FkCount.Done(n)
                : new FkCount.TimedOut();
        }

        changed();
        return true;
    }
}

/// <summary>Targets of FK jumps: how to open and label them (context menu, form).</summary>
public static class FkTargets
{
    /// <summary>Tables outside the browsed schemas (e.g. a referencing table in another schema) are opened by name.</summary>
    public static TableSummary SummaryOf(ActiveConnection active, TableRef table) =>
        active.Schema?.Find(table) ?? new TableSummary(table.Owner, table.Name, TableKind.Table);

    /// <summary>The name the user knows: without owner in the browsed schema (or through a synonym), qualified otherwise.</summary>
    public static string Label(ActiveConnection active, TableRef table)
    {
        var summary = SummaryOf(active, table);
        return summary.Synonym is not null || table.Owner == active.Schema?.Owner ? summary.DisplayName : table.ToString();
    }

    /// <summary>
    /// Tooltip of a "Referenced via" heading: which tables of the row (one, or several in a query result) extend
    /// <paramref name="via"/>.
    /// </summary>
    public static string ViaTitle(ActiveConnection active, TableRef via, IReadOnlyList<FkJump> jumps) =>
        TextFormat.Format(GridText.GridMenu_ReferencedViaTitle, Label(active, via),
            string.Join(", ", jumps.Select(j => j.Inherited?.Table).OfType<TableRef>().Distinct().Select(t => Label(active, t))));

    /// <summary>The relationship as the column menu names it (<see cref="ColumnFacts.Title"/>; review 3.54.0, B5), and why a jump is not offered.</summary>
    public static string Tooltip(FkJump jump) =>
        ColumnFacts.Title(jump.ForeignKey) +
        (jump.SkippedNote is { } skipped ? Environment.NewLine + skipped + "." : "") +
        (jump.Unavailable is { } reason ? Environment.NewLine + reason : "");
}
