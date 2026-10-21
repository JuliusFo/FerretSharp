using FerretSharp.Core.Schema;

namespace FerretSharp.Core.Data;

/// <summary>
/// What a workspace's session is doing while one of its statements runs (WP-52), read from <c>V$SESSION</c> and
/// <c>V$SESSION_LONGOPS</c> on the explorer session. Both parts are null when there is nothing to say: no lock in the way,
/// no long operation, or the session was not found (it runs on another RAC instance).
/// </summary>
/// <param name="Wait">The session waits for a lock another session holds.</param>
/// <param name="Progress">The latest unfinished long operation (full scan, hash join, sort) Oracle reports for the session.</param>
public sealed record SessionActivity(SessionWait? Wait, LongOperation? Progress)
{
    public static readonly SessionActivity None = new(null, null);
}

/// <summary>A wait for another session (<c>BLOCKING_SESSION</c>).</summary>
/// <param name="Event">Oracle's wait event, e.g. <c>enq: TX - row lock contention</c>.</param>
/// <param name="For">How long the current wait lasts so far; null if Oracle does not say.</param>
/// <param name="BlockerSid">The blocking session's SID.</param>
/// <param name="Blocker">Who that is; null if it is not visible (another RAC instance).</param>
/// <param name="Object">The table (or partition's table) of the row waited for; null if unknown or not visible to the user.</param>
public sealed record SessionWait(string Event, TimeSpan? For, int BlockerSid, LockHolder? Blocker, TableRef? Object);

/// <summary>One entry of <c>V$SESSION_LONGOPS</c>.</summary>
/// <param name="Operation">Oracle's name for it (<c>OPNAME</c>: "Table Scan", "Hash Join", "Sort Output"), not translated.</param>
/// <param name="Target">What it works on (<c>TARGET</c>, usually OWNER.TABLE); null for joins and sorts.</param>
/// <param name="Remaining">Oracle's estimate (<c>TIME_REMAINING</c>); null if it has none yet.</param>
public sealed record LongOperation(string Operation, string? Target, long Done, long Total, TimeSpan? Remaining)
{
    /// <summary>Done as a share of the total, 0 … 1.</summary>
    public double Fraction => Total <= 0 ? 0 : Math.Clamp((double)Done / Total, 0, 1);

    /// <summary>Whole percent, never 100 while it runs (99.6 % shows 99).</summary>
    public int Percent => Math.Min(99, (int)Math.Floor(Fraction * 100));
}
