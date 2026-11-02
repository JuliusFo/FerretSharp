using FerretSharp.Core.Query;
using FerretSharp.Core.Resources;
using FerretSharp.Core.Schema;

namespace FerretSharp.Core.Relations;

/// <summary>
/// The query a user builds in the relationship diagram (WP-69), as stored with the table tab: the tab's table is the
/// root, every further node is reached by a path of relationships from it. A node is a path, not a table – the same
/// table can come in twice (invoice and delivery customer). A step to a parent keeps one row per root row; a step to a
/// child chooses one child (filter, order, the first), so the rows of the root stay as they are as well.
/// </summary>
/// <param name="Nodes">The root (empty path) first, then the others; a node's path without its last step is a node too.</param>
public sealed record QueryDesignState(IReadOnlyList<QueryNodeState> Nodes)
{
    public static readonly QueryDesignState Empty = new([QueryNodeState.Root]);

    /// <summary>The diagram is in the mode "Abfrage bauen".</summary>
    public bool Active { get; init; }

    /// <summary>Names of the generated C# as the user changed them; empty for the ones from the root's entity.</summary>
    public QueryNames Names { get; init; } = QueryNames.None;

    /// <summary>The node at <paramref name="path"/>; null if the query has none there.</summary>
    public QueryNodeState? Find(IReadOnlyList<QueryStep> path) => Nodes.FirstOrDefault(n => QueryStep.SamePath(n.Path, path));

    /// <summary>
    /// Adds the node one step beyond <paramref name="from"/>; nothing if <paramref name="from"/> is not in the query or the
    /// node is there already.
    /// </summary>
    public QueryDesignState Add(IReadOnlyList<QueryStep> from, QueryStep step)
    {
        if (Find(from) is null)
        {
            return this;
        }

        IReadOnlyList<QueryStep> path = [.. from, step];
        return Find(path) is null ? this with { Nodes = [.. Nodes, new QueryNodeState(path)] } : this;
    }

    /// <summary>Removes the node at <paramref name="path"/> and everything reached through it; the root stays.</summary>
    public QueryDesignState Remove(IReadOnlyList<QueryStep> path) =>
        path.Count == 0 ? this : this with { Nodes = [.. Nodes.Where(n => !QueryStep.StartsWith(n.Path, path))] };

    /// <summary>The query with the node at <paramref name="path"/> changed; unchanged if there is none.</summary>
    public QueryDesignState Update(IReadOnlyList<QueryStep> path, Func<QueryNodeState, QueryNodeState> change) =>
        this with { Nodes = [.. Nodes.Select(n => QueryStep.SamePath(n.Path, path) ? change(n) : n)] };

    /// <summary>The state with what was set to null in the file by hand as empty, and with a root node in any case.</summary>
    public QueryDesignState Normalized()
    {
        var nodes = (Nodes ?? []).OfType<QueryNodeState>().Select(n => n with
        {
            Path = (n.Path ?? []).OfType<QueryStep>().Where(s => s.Relation is not null).ToList(),
            Columns = (n.Columns ?? []).OfType<QueryColumn>().Where(c => c.Column is not null).ToList(),
            Filters = (n.Filters ?? []).OfType<QueryFilter>().Where(f => f.Condition is not null).ToList(),
            Sorts = (n.Sorts ?? []).OfType<SortSpec>().ToList(),
        }).ToList();
        if (!nodes.Any(n => n.Path.Count == 0))
        {
            nodes.Insert(0, QueryNodeState.Root);
        }

        return this with { Nodes = nodes, Names = Names ?? QueryNames.None };
    }
}

/// <summary>The names of the generated C# of one query (WP-69); null or empty for the default.</summary>
public sealed record QueryNames(string? Class = null, string? Predicate = null, string? Projection = null, string? Dto = null)
{
    public static readonly QueryNames None = new();
}

/// <summary>One step of a path: the relationship (<see cref="RelationGraph.KeyOf"/>) and whether it leads to a child.</summary>
/// <param name="ToChild">
/// From the referenced table to the referencing one (one child is chosen); otherwise from the referencing table to the
/// referenced one (the parent). A self reference can go either way, so the direction is stored.
/// </param>
public sealed record QueryStep(string Relation, bool ToChild)
{
    public static bool SamePath(IReadOnlyList<QueryStep> a, IReadOnlyList<QueryStep> b) => a.Count == b.Count && StartsWith(a, b);

    /// <summary>Whether <paramref name="path"/> begins with <paramref name="prefix"/> (or is it).</summary>
    public static bool StartsWith(IReadOnlyList<QueryStep> path, IReadOnlyList<QueryStep> prefix) =>
        path.Count >= prefix.Count && prefix.Select((s, i) => s == path[i]).All(same => same);
}

/// <summary>A node of the query: its columns in the result, its conditions and – for a chosen child – its order.</summary>
public sealed record QueryNodeState(IReadOnlyList<QueryStep> Path)
{
    public static readonly QueryNodeState Root = new([]);

    public IReadOnlyList<QueryColumn> Columns { get; init; } = [];

    /// <summary>
    /// Conditions in the filter model of the grid (groups as WP-50). At the root and a parent they limit the root's rows,
    /// at a chosen child they decide which child is chosen.
    /// </summary>
    public IReadOnlyList<QueryFilter> Filters { get; init; } = [];

    /// <summary>The order of a chosen child (the first is taken); empty for the primary key. Not used elsewhere.</summary>
    public IReadOnlyList<SortSpec> Sorts { get; init; } = [];
}

/// <summary>A column in the result.</summary>
/// <param name="Name">The name of its value (DTO property, alias in the SQL) as the user changed it; null for the one from the path.</param>
public sealed record QueryColumn(string Column, string? Name = null);

/// <summary>A condition of a node.</summary>
/// <param name="Parameter">
/// The name of the parameter the value becomes in C# (the user's choice, "als Parameter"); null for a value written into
/// the code. A view has no parameters: such conditions stay out of it.
/// </param>
public sealed record QueryFilter(FilterCondition Condition, string? Parameter = null);

/// <summary>
/// A <see cref="QueryDesignState"/> resolved against the relationships the schema has now. What is gone (a relationship,
/// a table on the way) is left out with a note, like a tab on a dropped table.
/// </summary>
public sealed class QueryDesign
{
    private QueryDesign(TableRef root, IReadOnlyList<QueryNode> nodes, IReadOnlyList<string> notes, QueryDesignState state)
    {
        Root = root;
        Nodes = nodes;
        Notes = notes;
        State = state;
    }

    public TableRef Root { get; }

    /// <summary>The root first; every node after the node it is reached from.</summary>
    public IReadOnlyList<QueryNode> Nodes { get; }

    /// <summary>What was left out, one sentence each.</summary>
    public IReadOnlyList<string> Notes { get; }

    /// <summary>The state without what was left out – what the tab stores from now on.</summary>
    public QueryDesignState State { get; }

    public QueryNode? Find(IReadOnlyList<QueryStep> path) => Nodes.FirstOrDefault(n => QueryStep.SamePath(n.Path, path));

    /// <summary>The nodes reached from <paramref name="node"/> in one step.</summary>
    public IEnumerable<QueryNode> ChildrenOf(QueryNode node) => Nodes.Where(n => ReferenceEquals(n.From, node));

    /// <summary>
    /// The query without the columns, conditions and orders on columns its tables no longer have (each named in
    /// <see cref="Notes"/>). Tables whose details are not known yet (<paramref name="detailsOf"/> gives null) stay as they are.
    /// </summary>
    public QueryDesign WithColumnsOf(Func<TableRef, TableDetails?> detailsOf)
    {
        var notes = Notes.ToList();
        var changed = new Dictionary<QueryNode, QueryNodeState>(ReferenceEqualityComparer.Instance);
        foreach (var node in Nodes)
        {
            if (detailsOf(node.Table) is not { } details)
            {
                continue;
            }

            var gone = new SortedSet<string>(StringComparer.Ordinal);
            bool Has(string column)
            {
                if (details.IndexOf(column) >= 0)
                {
                    return true;
                }

                gone.Add(column);
                return false;
            }

            var state = node.State with
            {
                Columns = [.. node.State.Columns.Where(c => Has(c.Column))],
                Filters = [.. node.State.Filters.Where(f => Has(f.Condition.Column))],
                Sorts = [.. node.State.Sorts.Where(s => Has(s.Column))],
            };
            if (gone.Count > 0)
            {
                changed[node] = state;
                notes.AddRange(gone.Select(c => TextFormat.Format(RelationText.QueryColumnGone, Label(node), c)));
            }
        }

        if (changed.Count == 0)
        {
            return this;
        }

        var nodes = new List<QueryNode>(Nodes.Count);
        foreach (var node in Nodes)
        {
            var from = node.From is null ? null : nodes[node.From.Index];
            nodes.Add(node with { State = changed.GetValueOrDefault(node) ?? node.State, From = from });
        }

        return new QueryDesign(Root, nodes, notes, State with { Nodes = [.. nodes.Select(n => n.State)] });
    }

    public static QueryDesign Resolve(TableRef root, QueryDesignState state, RelationGraph graph)
    {
        var normalized = state.Normalized();
        var resolved = new List<QueryNode>();
        var notes = new List<string>();

        // Shorter paths first, so the node a step starts from is resolved before it; in their order otherwise.
        foreach (var nodeState in normalized.Nodes.OrderBy(n => n.Path.Count))
        {
            if (nodeState.Path.Count == 0)
            {
                if (resolved.Count == 0)
                {
                    resolved.Add(new QueryNode(nodeState, null, null, root, 0));
                }

                continue;
            }

            var from = resolved.FirstOrDefault(n => QueryStep.SamePath(n.Path, nodeState.Path.Take(nodeState.Path.Count - 1).ToList()));
            if (from is null || resolved.Any(n => QueryStep.SamePath(n.Path, nodeState.Path)))
            {
                // Below a node that was left out (noted there), or twice in the file.
                continue;
            }

            var step = nodeState.Path[^1];
            if (graph.Find(step.Relation) is not { } relation || StepTarget(relation, step.ToChild, from.Table) is not { } table)
            {
                notes.Add(TextFormat.Format(RelationText.QueryRelationGone, Label(from), RelationName(step.Relation)));
                continue;
            }

            resolved.Add(new QueryNode(nodeState, from, relation, table, 0));
        }

        // In the order of the tree – each node followed by what is reached through it – as the side panel lists them.
        var nodes = new List<QueryNode>(resolved.Count);
        void Visit(QueryNode node, QueryNode? from)
        {
            var placed = node with { From = from, Index = nodes.Count };
            nodes.Add(placed);
            foreach (var next in resolved.Where(n => ReferenceEquals(n.From, node)))
            {
                Visit(next, placed);
            }
        }

        Visit(resolved[0], null);
        return new QueryDesign(root, nodes, notes, normalized with { Nodes = [.. nodes.Select(n => n.State)] });
    }

    /// <summary>
    /// The step that follows <paramref name="relation"/> from a node on <paramref name="table"/>: to the parent if the
    /// table references, to a child if it is referenced; a self reference leads to the parent (the child is chosen
    /// through the node's list of relationships). Null if the relationship does not touch the table.
    /// </summary>
    public static QueryStep? StepFrom(TableRef table, ForeignKeyInfo relation) =>
        relation.From == table ? new QueryStep(RelationGraph.KeyOf(relation), false)
        : relation.To == table ? new QueryStep(RelationGraph.KeyOf(relation), true)
        : null;

    /// <summary>
    /// The steps a node can take: its parents, then its children (a self reference both ways), without those the query
    /// takes already and without the way back over the relationship that led to the node (E2E: from a chosen position
    /// back to its order).
    /// </summary>
    public IReadOnlyList<(QueryStep Step, ForeignKeyInfo Relation)> NextSteps(QueryNode node, RelationGraph graph)
    {
        var taken = ChildrenOf(node).Select(n => n.Step!).ToHashSet();
        if (node.Step is { } incoming)
        {
            taken.Add(incoming with { ToChild = !incoming.ToChild });
        }

        return
        [
            .. graph.ParentsOf(node.Table).Select(r => (Step: new QueryStep(RelationGraph.KeyOf(r), false), Relation: r))
                .Concat(graph.ChildrenOf(node.Table).Select(r => (Step: new QueryStep(RelationGraph.KeyOf(r), true), Relation: r)))
                .Where(s => !taken.Contains(s.Step)),
        ];
    }

    /// <summary>"AUFTRAG → KUNDE": the tables along the path of a node, to name it in notes.</summary>
    public static string Label(QueryNode node)
    {
        var tables = new List<string>();
        for (var n = node; n is not null; n = n.From)
        {
            tables.Add(n.Table.Name);
        }

        tables.Reverse();
        return string.Join(" → ", tables);
    }

    private static TableRef? StepTarget(ForeignKeyInfo relation, bool toChild, TableRef from) =>
        toChild ? relation.To == from ? relation.From : null
        : relation.From == from ? relation.To : null;

    /// <summary>The name part of a relationship key (<see cref="RelationGraph.KeyOf"/>): owner, table, name, source.</summary>
    private static string RelationName(string key) => key.Split('\0') is { Length: 4 } parts ? parts[2] : key;
}

/// <summary>A node of a resolved query.</summary>
/// <param name="From">The node the step starts from; null for the root.</param>
/// <param name="Relation">The relationship of the last step; null for the root.</param>
/// <param name="Index">Position in <see cref="QueryDesign.Nodes"/>; the root is 0.</param>
public sealed record QueryNode(QueryNodeState State, QueryNode? From, ForeignKeyInfo? Relation, TableRef Table, int Index)
{
    public IReadOnlyList<QueryStep> Path => State.Path;

    /// <summary>The last step; null for the root.</summary>
    public QueryStep? Step => Path.Count == 0 ? null : Path[^1];

    public bool IsRoot => From is null;

    /// <summary>A child chosen from the node before (filter, order, the first one).</summary>
    public bool IsChosenChild => Step is { ToChild: true };
}
