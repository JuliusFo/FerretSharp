using FerretSharp.Core.Schema;

namespace FerretSharp.Core.Relations;

/// <summary>
/// One step of a way between two tables (WP-74): over <paramref name="Relation"/> from <paramref name="From"/> to
/// <paramref name="To"/> – to the parent (the table referenced, n:1) or to a child (a table referencing, 1:n).
/// </summary>
public sealed record PathStep(ForeignKeyInfo Relation, bool ToChild, TableRef From, TableRef To)
{
    /// <summary>The step as the query of the relationship view stores it (WP-69).</summary>
    public QueryStep Query => new(RelationGraph.KeyOf(Relation), ToChild);

    /// <summary>"AUFTRAG.KUNDE_ID → KUNDEN.ID": the columns of the step, the table it starts from first.</summary>
    public string Text(Func<TableRef, string> label)
    {
        var (fromColumns, toColumns) = ToChild ? (Relation.ToColumns, Relation.FromColumns) : (Relation.FromColumns, Relation.ToColumns);
        return $"{Columns(label(From), fromColumns)} → {Columns(label(To), toColumns)}";
    }

    private static string Columns(string table, IReadOnlyList<string> columns) =>
        columns.Count == 1 ? $"{table}.{columns[0]}" : $"{table}.({string.Join(", ", columns)})";
}

/// <summary>
/// A way from <see cref="Start"/> to <see cref="Target"/> over relationships (WP-74): every table at most once, each step
/// to a parent or to a child.
/// </summary>
public sealed class RelationPath
{
    public RelationPath(TableRef start, IReadOnlyList<PathStep> steps)
    {
        if (steps.Count == 0)
        {
            throw new ArgumentException("A path has at least one step.", nameof(steps));
        }

        Start = start;
        Steps = steps;
        // With the table it leads to and the columns: two relationships of the C# model may share a key (review, @key).
        Key = string.Join('\u0002', steps.Select(s =>
            $"{RelationGraph.KeyOf(s.Relation)}\u0003{s.To.Owner}\u0001{s.To.Name}\u0003{string.Join('\u0001', s.Relation.FromColumns)}\u0003{string.Join('\u0001', s.Relation.ToColumns)}\u0003{(s.ToChild ? 'c' : 'p')}"));
        SourceRank = steps.Max(s => RankOf(s.Relation.Source));
    }

    public TableRef Start { get; }

    public TableRef Target => Steps[^1].To;

    public IReadOnlyList<PathStep> Steps { get; }

    public int Length => Steps.Count;

    /// <summary>Only steps to parents: each row of the start meets at most one row of the target ("1 : 1").</summary>
    public bool SingleRow => Steps.All(s => !s.ToChild);

    /// <summary>The weakest kind of relationship on the way: 0 constraints only, 1 the C# model, 2 others.</summary>
    public int SourceRank { get; }

    /// <summary>Identifies the path among others: its relationships and their directions.</summary>
    public string Key { get; }

    /// <summary>The tables along the path, the start first.</summary>
    public IEnumerable<TableRef> Tables => Steps.Select(s => s.From).Append(Target);

    /// <summary>One line per step (<see cref="PathStep.Text"/>), to paste into a ticket or a JOIN.</summary>
    public string Text(Func<TableRef, string> label) => string.Join("\n", Steps.Select(s => s.Text(label)));

    /// <summary>
    /// The path as a query of the relationship view (WP-69): a node for each part of it from the start, the target last.
    /// Into <paramref name="existing"/> if given – what it has already stays, only missing nodes come in.
    /// </summary>
    public QueryDesignState ToQuery(QueryDesignState? existing = null)
    {
        var state = (existing ?? QueryDesignState.Empty).Normalized();
        var path = new List<QueryStep>();
        foreach (var step in Steps)
        {
            state = state.Add([.. path], step.Query);
            path.Add(step.Query);
        }

        return state with { Active = true };
    }

    /// <summary>The query path of the target node in <see cref="ToQuery"/>.</summary>
    public IReadOnlyList<QueryStep> QueryPath => [.. Steps.Select(s => s.Query)];

    /// <summary>Shorter first; then "1 : 1" before "1 : n"; then constraints before other kinds.</summary>
    public static int Compare(RelationPath a, RelationPath b) =>
        a.Length != b.Length ? a.Length.CompareTo(b.Length)
        : a.SingleRow != b.SingleRow ? (a.SingleRow ? -1 : 1)
        : a.SourceRank.CompareTo(b.SourceRank);

    internal static int RankOf(FkSource source) => source switch
    {
        FkSource.Declared => 0,
        FkSource.ClrModel => 1,
        _ => 2,
    };
}

/// <summary>
/// All ways between two tables over the relationships of a graph (WP-74, ADR 0044), shortest first and without a limit:
/// <see cref="Next"/> hands out the next ones and stops there, the next call goes on. A way takes each table once,
/// leaves out self references and the tables to avoid (the start and the target excepted), and follows a relationship
/// either way. Two relationships between the same tables make two ways.
/// <para>
/// A breadth-first search from the target gives each table its distance to it; then a depth-first search runs length
/// for length from the shortest possible one and only goes on with a way while the target can still be reached in the
/// steps left. It ends by itself once a length was searched without cutting a way short: there are no longer ones.
/// The number of ways grows exponentially with the length – hence the blocks, the progress and the cancellation.
/// </para>
/// The walk keeps its own stack, so a cancelled block goes on where it stopped. Not thread-safe: one caller at a time
/// (the view runs it on a background thread, one block after the other).
/// </summary>
public sealed class RelationPathSearch
{
    private readonly Dictionary<TableRef, List<PathStep>> _steps = [];
    private readonly TableRef _start;
    private readonly TableRef _target;
    private readonly Stack<Frame> _stack = new();
    private readonly HashSet<TableRef> _visited = [];
    private readonly List<PathStep> _way = [];
    private Dictionary<TableRef, int>? _distance;
    private bool _cut;
    private int _length;
    private int _found;
    private bool _done;

    /// <param name="graph">The relationships to follow (only the kinds the view shows).</param>
    /// <param name="avoid">Tables no way goes through, e.g. who created a row – they would be on every way.</param>
    public RelationPathSearch(RelationGraph graph, TableRef start, TableRef target, IReadOnlySet<TableRef>? avoid = null)
    {
        _start = start;
        _target = target;
        bool Allowed(TableRef table) => table == start || table == target || avoid is null || !avoid.Contains(table);
        foreach (var relation in graph.Relations.Where(r => !RelationGraph.IsSelfReference(r) && Allowed(r.From) && Allowed(r.To)))
        {
            Add(new PathStep(relation, ToChild: false, relation.From, relation.To));
            Add(new PathStep(relation, ToChild: true, relation.To, relation.From));
        }

        // Parents first (they keep one row), constraints before the other kinds, then by name: the order ways come in.
        foreach (var list in _steps.Values)
        {
            list.Sort((a, b) =>
                a.ToChild != b.ToChild ? (a.ToChild ? 1 : -1)
                : RelationPath.RankOf(a.Relation.Source) is var ra && RelationPath.RankOf(b.Relation.Source) is var rb && ra != rb ? ra.CompareTo(rb)
                : RelationGraph.TableComparer.Instance.Compare(a.To, b.To) is var byTable and not 0 ? byTable
                : string.CompareOrdinal(a.Relation.Name, b.Relation.Name));
        }
    }

    /// <summary>The length searched now (the longest searched once <see cref="Done"/>); 0 before the first call.</summary>
    public int Length => Volatile.Read(ref _length);

    /// <summary>Ways handed out so far.</summary>
    public int Found => Volatile.Read(ref _found);

    /// <summary>Every way is found; <see cref="Next"/> returns nothing more.</summary>
    public bool Done => Volatile.Read(ref _done);

    /// <summary>The next ways, at most <paramref name="count"/>; fewer only once <see cref="Done"/>.</summary>
    /// <exception cref="OperationCanceledException">Cancelled; the search goes on where it was with the next call.</exception>
    public IReadOnlyList<RelationPath> Next(int count, CancellationToken cancellationToken)
    {
        var result = new List<RelationPath>(Math.Min(count, 64));
        while (result.Count < count && !Done)
        {
            if (Move(cancellationToken) is { } path)
            {
                result.Add(path);
                Interlocked.Increment(ref _found);
            }
        }

        return result;
    }

    private void Add(PathStep step)
    {
        if (!_steps.TryGetValue(step.From, out var list))
        {
            _steps[step.From] = list = [];
        }

        list.Add(step);
    }

    /// <summary>
    /// One move of the walk: a way found, or null for a move that found none (or the end, then <see cref="Done"/>). The
    /// token is checked before anything changes, so a cancellation leaves the walk as it was.
    /// </summary>
    private RelationPath? Move(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_distance is null)
        {
            var distance = DistancesToTarget(cancellationToken);
            if (_start == _target || !distance.TryGetValue(_start, out var shortest))
            {
                Volatile.Write(ref _done, true);
                return null;
            }

            _distance = distance;
            Begin(shortest);
            return null;
        }

        if (!_stack.TryPeek(out var frame))
        {
            // A length searched without cutting a way short: there are no longer ones.
            if (_cut)
            {
                Begin(_length + 1);
            }
            else
            {
                Volatile.Write(ref _done, true);
            }

            return null;
        }

        var steps = _steps.GetValueOrDefault(frame.Table);
        if (steps is null || frame.Index >= steps.Count)
        {
            _stack.Pop();
            if (_way.Count > 0)
            {
                _visited.Remove(frame.Table);
                _way.RemoveAt(_way.Count - 1);
            }

            return null;
        }

        var step = steps[frame.Index++];
        if (_visited.Contains(step.To) || !_distance.TryGetValue(step.To, out var left))
        {
            return null;
        }

        if (_way.Count + 1 + left > _length)
        {
            _cut = true; // a longer length may find it
            return null;
        }

        if (step.To == _target)
        {
            // The target ends a way, never a table to pass through; a shorter way came with an earlier length.
            return _way.Count + 1 == _length ? new RelationPath(_start, [.. _way, step]) : null;
        }

        _way.Add(step);
        _visited.Add(step.To);
        _stack.Push(new Frame(step.To));
        return null;
    }

    private void Begin(int length)
    {
        Volatile.Write(ref _length, length);
        _cut = false;
        _visited.Clear();
        _visited.Add(_start);
        _way.Clear();
        _stack.Push(new Frame(_start));
    }

    /// <summary>
    /// Steps from each table to the target, ignoring that a way takes each table once – never more than a way needs, so
    /// nothing is cut that would fit. Not through the start: no way comes back to it, so a table that reaches the target
    /// only over the start gets no distance and is never entered (review: it cut ways short at every length, and "all
    /// found" came only lengths later). Tables without a distance cannot reach the target at all.
    /// </summary>
    private Dictionary<TableRef, int> DistancesToTarget(CancellationToken cancellationToken)
    {
        var distance = new Dictionary<TableRef, int> { [_target] = 0 };
        var queue = new Queue<TableRef>([_target]);
        while (queue.TryDequeue(out var table))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (table == _start || !_steps.TryGetValue(table, out var steps))
            {
                continue;
            }

            // A step is followed both ways, so the steps from a table lead to the tables that reach it.
            foreach (var step in steps)
            {
                if (distance.TryAdd(step.To, distance[table] + 1))
                {
                    queue.Enqueue(step.To);
                }
            }
        }

        return distance;
    }

    /// <summary>A table on the way and the next of its steps to try.</summary>
    private sealed class Frame(TableRef table)
    {
        public TableRef Table { get; } = table;

        public int Index { get; set; }
    }
}
