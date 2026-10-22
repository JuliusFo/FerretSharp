using FerretSharp.Core.Schema;

namespace FerretSharp.Core.Relations;

/// <summary>
/// The relationships between the tables of one connection (WP-42): every <see cref="ForeignKeyInfo"/> the schema knows,
/// whatever its <see cref="FkSource"/> – declared constraints, the C# model's navigations, later manual and conventional
/// ones. A relationship points from the referencing table (child, <see cref="ForeignKeyInfo.From"/>) to the referenced
/// one (parent, <see cref="ForeignKeyInfo.To"/>). The graph holds no rows; <see cref="RowTraversal"/> follows it through the data.
/// </summary>
public sealed class RelationGraph
{
    private readonly ILookup<TableRef, ForeignKeyInfo> _parents;
    private readonly ILookup<TableRef, ForeignKeyInfo> _children;
    private readonly Dictionary<string, ForeignKeyInfo> _byKey;

    public RelationGraph(IEnumerable<ForeignKeyInfo> relations)
    {
        Relations = relations.OrderBy(Sort, StringComparer.Ordinal).ToList();
        _parents = Relations.ToLookup(r => r.From);
        _children = Relations.ToLookup(r => r.To);
        _byKey = [];
        foreach (var relation in Relations)
        {
            _byKey.TryAdd(KeyOf(relation), relation);
        }
    }

    /// <summary>The relationships of the connection as the schema has them now (declared ones first, then other sources).</summary>
    public static RelationGraph Of(SchemaCache schema) => new(schema.ForeignKeys);

    /// <summary>All relationships, ordered by referencing table and name.</summary>
    public IReadOnlyList<ForeignKeyInfo> Relations { get; }

    /// <summary>Relationships in which <paramref name="table"/> references other tables (its parents), a self reference too.</summary>
    public IEnumerable<ForeignKeyInfo> ParentsOf(TableRef table) => _parents[table];

    /// <summary>Relationships in which other tables reference <paramref name="table"/> (its children), a self reference too.</summary>
    public IEnumerable<ForeignKeyInfo> ChildrenOf(TableRef table) => _children[table];

    /// <summary>The relationship with this <see cref="KeyOf"/>; null if the schema no longer has it.</summary>
    public ForeignKeyInfo? Find(string key) => _byKey.GetValueOrDefault(key);

    /// <summary>
    /// Identifies a relationship across reloads of the schema (the records are rebuilt): referencing table, name and source.
    /// Constraint names are unique per owner, the C# model names a relationship after entity and navigation.
    /// </summary>
    public static string KeyOf(ForeignKeyInfo relation) => $"{relation.From.Owner}\0{relation.From.Name}\0{relation.Name}\0{relation.Source}";

    /// <summary>Identifies a path of relationships (children of children …) by the keys of its relationships.</summary>
    public static string PathKey(IReadOnlyList<ForeignKeyInfo> path) => string.Join('\u0002', path.Select(KeyOf));

    /// <summary>The row references a row of its own table (an employee's manager).</summary>
    public static bool IsSelfReference(ForeignKeyInfo relation) => relation.From == relation.To;

    /// <summary>
    /// <paramref name="tables"/> parents first, so rows can be inserted in this order without violating a relationship
    /// between them; among tables without an order between them alphabetically. Self references do not count – rows of
    /// such a table are put in order among themselves. Tables on a cycle (A references B, B references A) have no such
    /// order: the cycle is reported (not resolved, decision of the user), and they come last, like the tables below them.
    /// </summary>
    /// <param name="relations">The relationships that bind the order; those with a table outside <paramref name="tables"/> are ignored.</param>
    public static TableOrder Order(IEnumerable<TableRef> tables, IEnumerable<ForeignKeyInfo> relations)
    {
        var nodes = tables.Distinct().ToList();
        var inSet = nodes.ToHashSet();
        var binding = relations
            .Where(r => !IsSelfReference(r) && inSet.Contains(r.From) && inSet.Contains(r.To))
            .Distinct()
            .ToList();

        // Kahn: a table is ready once all its parents are placed.
        var waitingFor = nodes.ToDictionary(t => t, t => binding.Where(r => r.From == t).Select(r => r.To).ToHashSet());
        var ready = new SortedSet<TableRef>(nodes.Where(t => waitingFor[t].Count == 0), TableComparer.Instance);
        var ordered = new List<TableRef>(nodes.Count);
        while (ready.Count > 0)
        {
            var next = ready.Min!;
            ready.Remove(next);
            ordered.Add(next);
            foreach (var child in binding.Where(r => r.To == next).Select(r => r.From).Distinct())
            {
                if (waitingFor[child].Remove(next) && waitingFor[child].Count == 0)
                {
                    ready.Add(child);
                }
            }
        }

        var rest = nodes.Except(ordered).Order(TableComparer.Instance).ToList();
        return new TableOrder([.. ordered, .. rest], CyclesAmong(rest, binding));
    }

    /// <summary>The strongly connected parts (Tarjan) of the tables left over with more than one table: each is a cycle.</summary>
    private static List<RelationCycle> CyclesAmong(IReadOnlyList<TableRef> rest, IReadOnlyList<ForeignKeyInfo> binding)
    {
        var restSet = rest.ToHashSet();
        var edges = binding.Where(r => restSet.Contains(r.From) && restSet.Contains(r.To)).ToList();
        var index = new Dictionary<TableRef, int>();
        var low = new Dictionary<TableRef, int>();
        var stack = new Stack<TableRef>();
        var onStack = new HashSet<TableRef>();
        var cycles = new List<RelationCycle>();
        var counter = 0;

        void Visit(TableRef table)
        {
            index[table] = low[table] = counter++;
            stack.Push(table);
            onStack.Add(table);
            foreach (var parent in edges.Where(r => r.From == table).Select(r => r.To).Distinct().Order(TableComparer.Instance))
            {
                if (!index.ContainsKey(parent))
                {
                    Visit(parent);
                    low[table] = Math.Min(low[table], low[parent]);
                }
                else if (onStack.Contains(parent))
                {
                    low[table] = Math.Min(low[table], index[parent]);
                }
            }

            if (low[table] != index[table])
            {
                return;
            }

            var part = new List<TableRef>();
            TableRef member;
            do
            {
                member = stack.Pop();
                onStack.Remove(member);
                part.Add(member);
            }
            while (member != table);

            if (part.Count > 1)
            {
                var members = part.ToHashSet();
                cycles.Add(new RelationCycle(
                    [.. part.Order(TableComparer.Instance)],
                    [.. edges.Where(r => members.Contains(r.From) && members.Contains(r.To)).OrderBy(Sort, StringComparer.Ordinal)]));
            }
        }

        foreach (var table in rest)
        {
            if (!index.ContainsKey(table))
            {
                Visit(table);
            }
        }

        return [.. cycles.OrderBy(c => c.Tables[0], TableComparer.Instance)];
    }

    private static string Sort(ForeignKeyInfo relation) => $"{relation.From.Name}\0{relation.From.Owner}\0{relation.Name}\0{relation.Source}";

    /// <summary>Tables by name, then owner – as the explorer lists them.</summary>
    internal sealed class TableComparer : IComparer<TableRef>
    {
        public static readonly TableComparer Instance = new();

        public int Compare(TableRef? x, TableRef? y) =>
            x is null || y is null
                ? Comparer<object?>.Default.Compare(x, y)
                : string.CompareOrdinal(x.Name, y.Name) is var byName and not 0 ? byName : string.CompareOrdinal(x.Owner, y.Owner);
    }
}

/// <summary>Tables in an order to insert rows in (<see cref="RelationGraph.Order"/>).</summary>
/// <param name="Tables">All tables: parents first; those on or below a cycle last.</param>
/// <param name="Cycles">Cycles among the tables; with any, no order satisfies every relationship.</param>
public sealed record TableOrder(IReadOnlyList<TableRef> Tables, IReadOnlyList<RelationCycle> Cycles)
{
    public bool HasCycles => Cycles.Count > 0;
}

/// <summary>Tables that reference each other in a circle, with the relationships that close it.</summary>
public sealed record RelationCycle(IReadOnlyList<TableRef> Tables, IReadOnlyList<ForeignKeyInfo> Relations)
{
    /// <summary>"AUFTRAG, RECHNUNG": the tables of the cycle by name.</summary>
    public string Display => string.Join(", ", Tables.Select(t => t.Name));
}
