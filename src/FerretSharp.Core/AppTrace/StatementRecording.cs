namespace FerretSharp.Core.AppTrace;

/// <summary>When the same statement counts as repeated (N+1): at least <paramref name="Count"/> runs within <paramref name="Window"/>.</summary>
public sealed record RepeatRule(int Count, TimeSpan Window)
{
    /// <summary>Decided with the user (WP-33): 10 runs within 2 s.</summary>
    public static readonly RepeatRule Default = new(10, TimeSpan.FromSeconds(2));
}

/// <summary>The commands of one origin.</summary>
/// <param name="Time">Execution plus reading the rows.</param>
public sealed record OriginTotals(StatementOrigin Origin, int Count, TimeSpan Time, long Rows);

/// <summary>All runs of one statement text from one origin.</summary>
/// <param name="Time">Execution plus reading the rows, summed up.</param>
/// <param name="Max">The longest single run.</param>
/// <param name="Rows">Rows read (queries) or affected (other statements); see <see cref="StatementRecording"/>.</param>
/// <param name="Navigation">For lazy loading: the navigation that was loaded last (<c>Positionen</c>).</param>
/// <param name="Burst">The most runs seen within the window of the <see cref="RepeatRule"/>.</param>
/// <param name="Repeated">The statement ran at least as often as the rule says within its window (N+1).</param>
public sealed record StatementTotals(
    StatementOrigin Origin,
    string Text,
    int Count,
    TimeSpan Time,
    TimeSpan Max,
    long Rows,
    int Errors,
    string? LastError,
    string? Navigation,
    int Burst,
    bool Repeated,
    DateTimeOffset First,
    DateTimeOffset Last)
{
    public TimeSpan Average => Count == 0 ? TimeSpan.Zero : Time / Count;
}

/// <summary>A consistent copy of a recording, safe to read on any thread.</summary>
/// <param name="Since">The first event after the start or the last reset; null if none came yet.</param>
/// <param name="Truncated">More different statements came than a recording keeps; the others only count in <see cref="Origins"/>.</param>
/// <param name="Version">Grows with every change; equal versions are equal snapshots.</param>
public sealed record RecordingSnapshot(
    int Count,
    TimeSpan Time,
    IReadOnlyList<OriginTotals> Origins,
    IReadOnlyList<StatementTotals> Statements,
    DateTimeOffset? Since,
    string? DataSource,
    bool Truncated,
    long Version)
{
    public static readonly RecordingSnapshot Empty = new(0, TimeSpan.Zero, [], [], null, null, false, 0);

    /// <summary>The origin's share of the whole time, 0 … 1.</summary>
    public double Share(OriginTotals origin) => Time > TimeSpan.Zero ? origin.Time / Time : 0;
}

/// <summary>
/// The statements of a traced app (WP-33), built from its EF Core events (<see cref="EfEvents"/>): the origin of each
/// command, totals per origin and per statement text, and repeated statements (N+1).
/// <list type="bullet">
/// <item>Lazy loading: EF raises <see cref="EfEvents.NavigationLazyLoading"/> right before the query that loads the
/// navigation, in the same context – the next command of that context is the lazy load.</item>
/// <item>Time: <see cref="EfEvents.CommandExecuted"/> ends when the reader is returned; the time to read the rows comes
/// with <see cref="EfEvents.DataReaderDisposing"/> (matched by command id) and is added.</item>
/// <item>Rows: a reader counts every <c>Read()</c>, including the last one that returns false – the rows are one less
/// (a reader closed before its end is counted one row short). Non-queries take the affected rows.</item>
/// </list>
/// Events arrive on the source's thread, snapshots are taken on the UI thread: everything runs under one lock.
/// </summary>
public sealed class StatementRecording(RepeatRule? rule = null)
{
    /// <summary>Different statement texts kept per recording; further ones only count per origin.</summary>
    public const int MaxStatements = 2000;

    /// <summary>Readers waiting for their end, and contexts waiting for their lazy load, at most.</summary>
    private const int MaxPending = 10_000;

    private readonly RepeatRule _rule = rule ?? RepeatRule.Default;
    private readonly Lock _lock = new();
    private readonly Dictionary<(StatementOrigin, string), Entry> _statements = [];
    private readonly Dictionary<StatementOrigin, Totals> _origins = [];
    private readonly Dictionary<string, Pending> _readers = [];
    private readonly Queue<string> _readerOrder = new();
    private readonly Dictionary<string, string> _lazyByContext = [];
    private int _count;
    private TimeSpan _time;
    private DateTimeOffset? _since;
    private string? _dataSource;
    private bool _truncated;
    private long _version;

    /// <summary>The <see cref="RecordingSnapshot.Version"/> a snapshot would have now – cheap, to skip taking an equal one.</summary>
    public long Version
    {
        get
        {
            lock (_lock)
            {
                return _version;
            }
        }
    }

    /// <summary>Takes one event in; false if it changed nothing (unknown event, reader of an unknown command).</summary>
    public bool Apply(EfEvent e)
    {
        lock (_lock)
        {
            var changed = e.Name switch
            {
                EfEvents.CommandExecuted => Executed(e, error: false),
                EfEvents.CommandError => Executed(e, error: true),
                EfEvents.DataReaderDisposing => ReaderClosed(e),
                EfEvents.NavigationLazyLoading => LazyLoading(e),
                _ => false,
            };
            if (changed)
            {
                _version++;
            }

            return changed;
        }
    }

    /// <summary>Starts over: everything recorded so far is forgotten.</summary>
    public void Reset()
    {
        lock (_lock)
        {
            _statements.Clear();
            _origins.Clear();
            _readers.Clear();
            _readerOrder.Clear();
            _lazyByContext.Clear();
            _count = 0;
            _time = TimeSpan.Zero;
            _since = null;
            _truncated = false;
            _version++;
        }
    }

    public RecordingSnapshot Snapshot()
    {
        lock (_lock)
        {
            return new RecordingSnapshot(
                _count,
                _time,
                [.. _origins.OrderByDescending(o => o.Value.Time).ThenBy(o => o.Key).Select(o => new OriginTotals(o.Key, o.Value.Count, o.Value.Time, o.Value.Rows))],
                [.. _statements.Values.OrderByDescending(s => s.Time).ThenBy(s => s.Text, StringComparer.Ordinal).Select(s => s.ToTotals(_rule))],
                _since,
                _dataSource,
                _truncated,
                _version);
        }
    }

    private bool Executed(EfEvent e, bool error)
    {
        var text = e.Field(EfEvents.Text);
        if (text is null)
        {
            return false;
        }

        var context = e.Field(EfEvents.Context);
        string? navigation = null;
        var origin = context is not null && _lazyByContext.Remove(context, out navigation)
            ? StatementOrigin.LazyLoading
            : StatementOrigins.FromCommandSource(e.Field(EfEvents.Source));
        var duration = e.Duration(EfEvents.Duration) ?? TimeSpan.Zero;
        var rows = !error && e.Field(EfEvents.Method) == "ExecuteNonQuery" && e.Int(EfEvents.Result) is > 0 and var affected ? affected : 0;

        _since ??= e.Time;
        _dataSource = e.Field(EfEvents.DataSource) ?? _dataSource;
        _count++;
        _time += duration;
        var totals = OriginOf(origin);
        totals.Count++;
        totals.Time += duration;
        totals.Rows += rows;

        var entry = EntryOf(origin, text, e.Time);
        if (entry is not null)
        {
            entry.Run(duration, rows, e.Time, _rule);
            entry.Navigation = navigation ?? entry.Navigation;
            if (error)
            {
                entry.Errors++;
                entry.LastError = e.Field(EfEvents.Error);
            }
        }

        if (!error && e.Field(EfEvents.CommandId) is { } id && e.Field(EfEvents.Method) is null or "ExecuteReader")
        {
            Remember(id, new Pending(origin, entry, duration));
        }

        return true;
    }

    private bool ReaderClosed(EfEvent e)
    {
        if (e.Field(EfEvents.CommandId) is not { } id || !_readers.Remove(id, out var pending))
        {
            return false;
        }

        var reading = e.Duration(EfEvents.Duration) ?? TimeSpan.Zero;
        var rows = Math.Max((e.Int(EfEvents.ReadCount) ?? 0) - 1, 0);
        _time += reading;
        var totals = OriginOf(pending.Origin);
        totals.Time += reading;
        totals.Rows += rows;
        if (pending.Entry is { } entry)
        {
            entry.Time += reading;
            entry.Rows += rows;
            entry.Max = Max(entry.Max, pending.Executed + reading);
        }

        return true;
    }

    private bool LazyLoading(EfEvent e)
    {
        if (e.Field(EfEvents.Context) is not { } context)
        {
            return false;
        }

        if (_lazyByContext.Count >= MaxPending)
        {
            _lazyByContext.Clear(); // contexts that never ran their load (disposed in between); losing them only misses a label
        }

        _lazyByContext[context] = e.Field(EfEvents.Navigation) ?? "";
        return false; // nothing to show until the command comes
    }

    private void Remember(string id, Pending pending)
    {
        if (_readerOrder.Count >= MaxPending)
        {
            _readers.Remove(_readerOrder.Dequeue()); // a reader that was never disposed: its reading time is lost
        }

        _readers[id] = pending;
        _readerOrder.Enqueue(id);
    }

    private Totals OriginOf(StatementOrigin origin)
    {
        if (!_origins.TryGetValue(origin, out var totals))
        {
            _origins[origin] = totals = new Totals();
        }

        return totals;
    }

    private Entry? EntryOf(StatementOrigin origin, string text, DateTimeOffset time)
    {
        if (_statements.TryGetValue((origin, text), out var entry))
        {
            return entry;
        }

        if (_statements.Count >= MaxStatements)
        {
            _truncated = true;
            return null;
        }

        return _statements[(origin, text)] = new Entry(origin, text, time);
    }

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

    private sealed class Totals
    {
        public int Count;
        public TimeSpan Time;
        public long Rows;
    }

    private sealed record Pending(StatementOrigin Origin, Entry? Entry, TimeSpan Executed);

    private sealed class Entry(StatementOrigin origin, string text, DateTimeOffset first)
    {
        /// <summary>A burst is counted up to this (a statement in a tight loop would otherwise keep every time of the window).</summary>
        private const int MaxBurst = 1000;

        private readonly Queue<DateTimeOffset> _recent = new();

        public string Text => text;
        public int Count;
        public TimeSpan Time;
        public TimeSpan Max;
        public long Rows;
        public int Errors;
        public string? LastError;
        public string? Navigation;
        public int Burst;
        public DateTimeOffset First { get; } = first;
        public DateTimeOffset Last = first;

        public void Run(TimeSpan duration, long rows, DateTimeOffset at, RepeatRule rule)
        {
            Count++;
            Time += duration;
            Rows += rows;
            Max = StatementRecording.Max(Max, duration);
            Last = at > Last ? at : Last;

            _recent.Enqueue(at);
            while (_recent.Count > 0 && (at - _recent.Peek() > rule.Window || _recent.Count > MaxBurst))
            {
                _recent.Dequeue();
            }

            Burst = Math.Max(Burst, _recent.Count);
        }

        public StatementTotals ToTotals(RepeatRule rule) =>
            new(origin, text, Count, Time, Max, Rows, Errors, LastError, Navigation, Burst, Burst >= rule.Count, First, Last);
    }
}
