namespace FerretSharp.Core.ServerLoad;

/// <summary>
/// The load of one server over time and the statements that caused it (WP-75, ADR 0045), from the counters the page reads
/// every look. Oracle's counters only grow, so everything here is a difference between two looks: the load as averages per
/// second, the statements as what each cursor did since the look before. The cursors need a baseline (all of them, read
/// once): a cursor first seen later was loaded into the shared pool meanwhile and counts from 0 – without the baseline a
/// single expensive run would become its own starting point and never show up.
/// </summary>
/// <remarks>
/// Kept per connection while the app runs; the page's session comes and goes. A look after a gap (page closed) spans the
/// whole gap: it counts towards „since opened“, not towards a window it does not lie in completely. Thread-safe: the looks
/// come from the pool, the page reads on its own thread.
/// </remarks>
public sealed class ServerLoadTracker
{
    /// <summary>How far back the load and the windows of the top list reach.</summary>
    public static readonly TimeSpan HistoryLength = TimeSpan.FromHours(1);

    /// <summary>Looks further apart than this leave a gap in the history.</summary>
    public static readonly TimeSpan GapAfter = TimeSpan.FromMinutes(2);

    private readonly Lock _gate = new();
    private readonly Dictionary<CursorKey, StatementFigures> _cursors = [];
    private readonly Dictionary<string, Activity> _sinceStart = [];
    private readonly List<Look> _looks = [];
    private readonly List<LoadSample> _history = [];
    private readonly Dictionary<string, StatementInfo?> _info = [];
    private LoadCounters? _load;
    private DateTime? _statementsAt;

    /// <summary>Server time of the first baseline: the start of „since opened“.</summary>
    public DateTime? StartedAt { get; private set; }

    /// <summary>Server time of the latest look.</summary>
    public DateTime? LastLookAt
    {
        get
        {
            lock (_gate)
            {
                return _load?.At;
            }
        }
    }

    /// <summary>No baseline yet, or the instance restarted: read all cursors and call <see cref="Rebase"/>.</summary>
    public bool NeedsBaseline { get; private set; } = true;

    /// <summary>Server time of the cursor figures held: read the cursors active since then.</summary>
    public DateTime? StatementsSince
    {
        get
        {
            lock (_gate)
            {
                return _statementsAt;
            }
        }
    }

    /// <summary>The load between the looks, oldest first, for <see cref="HistoryLength"/>.</summary>
    public IReadOnlyList<LoadSample> History
    {
        get
        {
            lock (_gate)
            {
                return [.. _history];
            }
        }
    }

    public LoadSample? Latest
    {
        get
        {
            lock (_gate)
            {
                return _history.Count > 0 ? _history[^1] : null;
            }
        }
    }

    /// <summary>The figures of all cursors at <paramref name="load"/>'s time: from here on they count.</summary>
    public void Rebase(LoadCounters load, IReadOnlyList<StatementCounters> all)
    {
        lock (_gate)
        {
            _cursors.Clear();
            foreach (var cursor in all)
            {
                _cursors[CursorKey.Of(cursor)] = cursor.Figures;
            }

            _load = load;
            _statementsAt = load.At;
            StartedAt ??= load.At;
            NeedsBaseline = false;
        }
    }

    /// <summary>
    /// The server's counters of a new look. Null for the first look and after a restart of the instance (the counters began
    /// again; <see cref="NeedsBaseline"/> is then true) – otherwise the load since the look before.
    /// </summary>
    public LoadSample? AddLoad(LoadCounters load)
    {
        lock (_gate)
        {
            var before = _load;
            _load = load;
            if (before is null || Restarted(before, load))
            {
                NeedsBaseline = true;
                return null;
            }

            var seconds = (load.At - before.At).TotalSeconds;
            if (seconds <= 0)
            {
                return null; // the server's clock went back (daylight saving time ended): no average over a negative time
            }

            var sample = Sample(before, load, seconds);
            _history.Add(sample);
            _history.RemoveAll(s => s.To < load.At - HistoryLength);
            return sample;
        }
    }

    /// <summary>
    /// The cursors active since <see cref="StatementsSince"/>, as read in the latest look: what each did since then. A cursor
    /// not held, or with a smaller figure than held, was loaded (again) meanwhile and counts from 0. Needs a baseline.
    /// </summary>
    public void AddStatements(IReadOnlyList<StatementCounters> changed)
    {
        lock (_gate)
        {
            if (NeedsBaseline || _load is null || _statementsAt is not { } from)
            {
                throw new InvalidOperationException("The statements need a baseline first.");
            }

            var to = _load.At;
            var look = new Look(from, to, []);
            foreach (var cursor in changed)
            {
                var key = CursorKey.Of(cursor);
                var delta = _cursors.TryGetValue(key, out var held) && !cursor.Figures.AnyBelow(held) ? cursor.Figures - held : cursor.Figures;
                _cursors[key] = cursor.Figures;
                if (delta.IsZero)
                {
                    continue;
                }

                Count(look.Deltas, cursor.SqlId, cursor.PlanHash, delta, to);
                Count(_sinceStart, cursor.SqlId, cursor.PlanHash, delta, to);
            }

            _looks.Add(look);
            _looks.RemoveAll(l => l.From < to - HistoryLength);
            _statementsAt = to;
        }
    }

    /// <summary>
    /// Statements with figures whose text, module and schema were not asked for yet, the most expensive (CPU) first, at
    /// most <paramref name="max"/>.
    /// </summary>
    public IReadOnlyList<string> SqlIdsWithoutInfo(int max)
    {
        lock (_gate)
        {
            return [.. _sinceStart.Where(s => !_info.ContainsKey(s.Key)).OrderByDescending(s => s.Value.Figures.CpuTime).ThenBy(s => s.Key, StringComparer.Ordinal)
                .Take(max).Select(s => s.Key)];
        }
    }

    /// <summary>The answer to <paramref name="asked"/>: a statement missing from <paramref name="found"/> left the shared pool.</summary>
    public void AddInfo(IReadOnlyList<string> asked, IReadOnlyList<StatementInfo> found)
    {
        lock (_gate)
        {
            foreach (var sqlId in asked)
            {
                _info.TryAdd(sqlId, null);
            }

            foreach (var info in found)
            {
                _info[info.SqlId] = info;
            }
        }
    }

    /// <summary>
    /// The top <paramref name="count"/> statements of the last <paramref name="window"/> (null: since opened), sorted
    /// descending by <paramref name="sort"/>. A window counts the looks lying in it completely.
    /// </summary>
    public IReadOnlyList<StatementRow> Top(TimeSpan? window, TopStatementSort sort, StatementFilter filter, int count)
    {
        lock (_gate)
        {
            Dictionary<string, Activity> source;
            if (window is { } length && _load is { } load)
            {
                source = [];
                foreach (var look in _looks.Where(l => l.From >= load.At - length))
                {
                    foreach (var (sqlId, activity) in look.Deltas)
                    {
                        Merge(source, sqlId, activity);
                    }
                }
            }
            else
            {
                source = _sinceStart;
            }

            return [.. source
                .Select(s => (s.Key, s.Value, Info: _info.GetValueOrDefault(s.Key)))
                .Where(s => filter.Matches(s.Info))
                .OrderByDescending(s => s.Value.Figures.ValueOf(sort))
                .ThenBy(s => s.Key, StringComparer.Ordinal)
                .Take(count)
                .Select(s => new StatementRow(s.Key, s.Value.Plans.Count, s.Value.Figures, s.Info, s.Value.LastActive))];
        }
    }

    /// <summary>The instance restarted between two looks: every counter began again.</summary>
    private static bool Restarted(LoadCounters before, LoadCounters now) =>
        now.StartedAt != before.StartedAt || now.DbTime < before.DbTime || now.Executions < before.Executions;

    private LoadSample Sample(LoadCounters before, LoadCounters now, double seconds)
    {
        double PerSecond(long newer, long older) => Math.Max(0, newer - older) / seconds;

        double? hostCpu = null;
        if (now.BusyTime is { } busy && now.IdleTime is { } idle && before.BusyTime is { } busyBefore && before.IdleTime is { } idleBefore)
        {
            var used = Math.Max(0, busy - busyBefore);
            var all = used + Math.Max(0, idle - idleBefore);
            hostCpu = all > 0 ? (double)used / all : null;
        }

        var waiting = now.WaitTime.ToDictionary(w => w.Key, w => PerSecond(w.Value, before.WaitTime.GetValueOrDefault(w.Key)) / 1e6, StringComparer.Ordinal);
        var previous = _history.Count > 0 ? _history[^1].To : (DateTime?)null;
        return new LoadSample(
            before.At, now.At, now.Cpus, hostCpu,
            PerSecond(now.DbTime, before.DbTime) / 1e6,
            PerSecond(now.DbCpu, before.DbCpu) / 1e6,
            PerSecond(now.Executions, before.Executions),
            PerSecond(now.LogicalReads, before.LogicalReads),
            PerSecond(now.Commits, before.Commits),
            waiting,
            AfterGap: now.At - before.At > GapAfter || (previous is { } end && before.At > end));
    }

    /// <summary>A cursor: one plan of a statement, in a container database per container.</summary>
    private readonly record struct CursorKey(string SqlId, long PlanHash, int ContainerId)
    {
        public static CursorKey Of(StatementCounters cursor) => new(cursor.SqlId, cursor.PlanHash, cursor.ContainerId);
    }

    /// <summary>What a statement did: its figures summed over its cursors, the plans it used, when it was last seen active.</summary>
    private sealed class Activity
    {
        public StatementFigures Figures;
        public readonly HashSet<long> Plans = [];
        public DateTime LastActive;
    }

    /// <summary>One look: what the statements did between <paramref name="From"/> and <paramref name="To"/>.</summary>
    private sealed record Look(DateTime From, DateTime To, Dictionary<string, Activity> Deltas);

    private static Activity Entry(Dictionary<string, Activity> map, string sqlId)
    {
        if (!map.TryGetValue(sqlId, out var activity))
        {
            map[sqlId] = activity = new Activity();
        }

        return activity;
    }

    private static void Count(Dictionary<string, Activity> map, string sqlId, long plan, StatementFigures delta, DateTime at)
    {
        var activity = Entry(map, sqlId);
        activity.Figures += delta;
        activity.Plans.Add(plan);
        activity.LastActive = at > activity.LastActive ? at : activity.LastActive;
    }

    private static void Merge(Dictionary<string, Activity> map, string sqlId, Activity other)
    {
        var activity = Entry(map, sqlId);
        activity.Figures += other.Figures;
        activity.Plans.UnionWith(other.Plans);
        activity.LastActive = other.LastActive > activity.LastActive ? other.LastActive : activity.LastActive;
    }
}
