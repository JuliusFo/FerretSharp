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
/// <param name="maxStatements">
/// Statements kept for „since opened“ with their text (review of WP-75): an app sending literals instead of binds makes new
/// SQL_IDs all day. Past it, the cheapest (CPU) go, down to nine tenths, so this does not sort on every look.
/// </param>
public sealed class ServerLoadTracker(int maxStatements = ServerLoadTracker.DefaultMaxStatements)
{
    public const int DefaultMaxStatements = 20_000;

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
    private DateTime? _fullAt;
    private long _version;
    private (long Version, TimeSpan? Window, TopStatementSort Sort, StatementFilter Filter, int Count, IReadOnlyList<StatementRow> Rows)? _top;

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

    /// <summary>
    /// An hour since all cursors were read (second review of WP-75): read them all again (<c>AddStatements(all, complete: true)</c>),
    /// which drops those gone from <c>V$SQLSTATS</c> – else every cursor ever seen stays, with literal SQL tens of thousands an
    /// hour. Dropping a cursor still there would count all its figures since loading again when it comes back.
    /// </summary>
    public bool FullReadDue
    {
        get
        {
            lock (_gate)
            {
                return _load is { } load && _fullAt is { } full && load.At - full >= HistoryLength;
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
            foreach (var (key, statement) in Statements(all))
            {
                _cursors[key] = statement.Figures;
            }

            _load = load;
            _statementsAt = load.At;
            _fullAt = load.At;
            StartedAt ??= load.At;
            NeedsBaseline = false;
            _version++;
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
    /// The cursors active since <see cref="StatementsSince"/>, as read in the latest look: what each statement did since then
    /// (its rows summed, whatever plan they name). A statement not held, or with a smaller figure than held, was loaded (again)
    /// meanwhile and counts from 0. Needs a baseline.
    /// </summary>
    /// <param name="complete">
    /// <paramref name="changed"/> holds every cursor of <c>V$SQLSTATS</c> (<see cref="FullReadDue"/>): those held but missing are
    /// gone and dropped; should one come back, it was loaded again and counts from 0.
    /// </param>
    public void AddStatements(IReadOnlyList<StatementCounters> changed, bool complete = false)
    {
        lock (_gate)
        {
            if (NeedsBaseline || _load is null || _statementsAt is not { } from)
            {
                throw new InvalidOperationException("The statements need a baseline first.");
            }

            var to = _load.At;
            var look = new Look(from, to, []);
            var read = Statements(changed);
            foreach (var (key, statement) in read)
            {
                var figures = statement.Figures;
                var delta = _cursors.TryGetValue(key, out var held) && !figures.AnyBelow(held) ? figures - held : figures;
                _cursors[key] = figures;
                if (delta.IsZero)
                {
                    continue;
                }

                Count(look.Deltas, key.SqlId, statement.Plans, delta, to);
                Count(_sinceStart, key.SqlId, statement.Plans, delta, to);
            }

            if (complete)
            {
                var present = read.Keys.ToHashSet();
                foreach (var gone in _cursors.Keys.Where(k => !present.Contains(k)).ToList())
                {
                    _cursors.Remove(gone);
                }

                _fullAt = to;
            }

            _looks.Add(look);
            _looks.RemoveAll(l => l.From < to - HistoryLength);
            _statementsAt = to;
            Trim();
            DropUnusedInfo();
            _version++;
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
            // Also those of the window that left „since opened“ (second review of WP-75): a window sorted by executions or reads
            // can show one at the top.
            var cpu = new Dictionary<string, long>();
            foreach (var (sqlId, activity) in _sinceStart.Concat(_looks.SelectMany(l => l.Deltas)))
            {
                cpu[sqlId] = Math.Max(cpu.GetValueOrDefault(sqlId), activity.Figures.CpuTime);
            }

            return [.. cpu.Where(s => !_info.ContainsKey(s.Key)).OrderByDescending(s => s.Value).ThenBy(s => s.Key, StringComparer.Ordinal)
                .Take(max).Select(s => s.Key)];
        }
    }

    /// <summary>The answer to <paramref name="asked"/>: a statement missing from <paramref name="found"/> left the shared pool.</summary>
    public void AddInfo(IReadOnlyList<string> asked, IReadOnlyList<StatementInfo> found)
    {
        lock (_gate)
        {
            foreach (var sqlId in asked.Where(Known))
            {
                _info.TryAdd(sqlId, null);
            }

            foreach (var info in found.Where(i => Known(i.SqlId)))
            {
                _info[info.SqlId] = info;
            }

            _version++;
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
            // The page renders often (also on every change of the app trace): only a new look computes the list anew.
            if (_top is { } cached && cached.Version == _version && cached.Window == window && cached.Sort == sort && cached.Filter == filter
                && cached.Count == count)
            {
                return cached.Rows;
            }

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

            IReadOnlyList<StatementRow> rows = [.. source
                .Select(s => (s.Key, s.Value, Info: _info.GetValueOrDefault(s.Key)))
                .Where(s => filter.Matches(s.Info))
                .OrderByDescending(s => s.Value.Figures.ValueOf(sort))
                .ThenBy(s => s.Key, StringComparer.Ordinal)
                .Take(count)
                .Select(s => new StatementRow(s.Key, s.Value.Plans.Count, s.Value.Figures, s.Info, s.Value.LastActive))];
            _top = (_version, window, sort, filter, count, rows);
            return rows;
        }
    }

    /// <summary>Texts held (for tests: they must go again).</summary>
    internal int InfoCount
    {
        get
        {
            lock (_gate)
            {
                return _info.Count;
            }
        }
    }

    /// <summary>
    /// The texts of statements in neither „since opened“ nor a window go (third review of WP-75): one trimmed from „since
    /// opened“ keeps its text only while a window shows it – else with literal SQL they would pile up again.
    /// </summary>
    private void DropUnusedInfo()
    {
        var inLooks = _looks.SelectMany(l => l.Deltas.Keys).ToHashSet(StringComparer.Ordinal);
        foreach (var sqlId in _info.Keys.Where(k => !_sinceStart.ContainsKey(k) && !inLooks.Contains(k)).ToList())
        {
            _info.Remove(sqlId);
        }
    }

    /// <summary>A statement of „since opened“ or of a window: one whose info is worth keeping.</summary>
    private bool Known(string sqlId) => _sinceStart.ContainsKey(sqlId) || _looks.Any(l => l.Deltas.ContainsKey(sqlId));

    /// <summary>
    /// Past <c>maxStatements</c>, the cheapest statements (CPU) leave „since opened“ with their text, down to nine tenths; one
    /// still in a window keeps its text.
    /// </summary>
    private void Trim()
    {
        if (_sinceStart.Count <= maxStatements)
        {
            return;
        }

        foreach (var sqlId in _sinceStart.OrderBy(s => s.Value.Figures.CpuTime).ThenBy(s => s.Key, StringComparer.Ordinal)
                     .Take(_sinceStart.Count - maxStatements * 9 / 10).Select(s => s.Key).ToList())
        {
            _sinceStart.Remove(sqlId);
            if (!Known(sqlId))
            {
                _info.Remove(sqlId);
            }
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

    /// <summary>
    /// A statement, in a container database per container – not per plan: <c>V$SQLSTATS</c> keeps one row per SQL_ID whose
    /// <c>PLAN_HASH_VALUE</c> follows the child cursor active last while its figures run on over all plans (E2E after WP-75:
    /// a bind-aware statement with five plans in <c>V$SQL</c>, one row here). Keyed by plan, every plan not seen before counted
    /// the statement's whole figures since loading again – twice within 15 minutes.
    /// </summary>
    private readonly record struct CursorKey(string SqlId, int ContainerId);

    /// <summary>
    /// The rows read, summed per statement with the plans they name. Should a version keep a row per plan, the sum still only
    /// grows; <see cref="IServerLoadReader.ReadStatementsAsync"/> then brings every row of a statement active since.
    /// </summary>
    private static Dictionary<CursorKey, (StatementFigures Figures, HashSet<long> Plans)> Statements(IReadOnlyList<StatementCounters> rows)
    {
        var statements = new Dictionary<CursorKey, (StatementFigures Figures, HashSet<long> Plans)>();
        foreach (var row in rows)
        {
            var key = new CursorKey(row.SqlId, row.ContainerId);
            var (figures, plans) = statements.TryGetValue(key, out var known) ? known : (default, []);
            plans.Add(row.PlanHash);
            statements[key] = (figures + row.Figures, plans);
        }

        return statements;
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

    private static void Count(Dictionary<string, Activity> map, string sqlId, IEnumerable<long> plans, StatementFigures delta, DateTime at)
    {
        var activity = Entry(map, sqlId);
        activity.Figures += delta;
        activity.Plans.UnionWith(plans);
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
