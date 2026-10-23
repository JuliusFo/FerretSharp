using FerretSharp.Core.Data;
using FerretSharp.Core.Resources;
using FerretSharp.Core.Schema;

namespace FerretSharp.Core.Relations;

/// <summary>
/// The record diagram (WP-60): rows instead of tables. The start rows stand in the middle, the rows they reference to the
/// left (parents), those that reference them to the right (children); each shown row can be expanded on either side, along
/// all relationships of that side or one. Rows are read on the read path of the workspace's session as the user expands
/// (<see cref="RowTraversal.FollowAsync"/>) and kept in a <see cref="RowGraph"/>, values only in memory. More new rows
/// along one relationship of one row than <see cref="GroupLimit"/> become one group node ("POSITION ×200") until opened.
/// What the user did is kept as <see cref="Steps"/> to build the diagram again (F5, a restored tab). Not thread-safe: the
/// UI thread changes it, one expansion at a time.
/// </summary>
public sealed class RecordDiagram
{
    /// <summary>Rows a record diagram holds at most (decision of the user; a copy may hold 10 000).</summary>
    public const int DefaultMaxRows = 2_000;

    public const int DefaultGroupLimit = DiagramOptions.DefaultGroupLimit;

    private readonly RowTraversal _traversal;
    private readonly RelationGraph _relations;
    private readonly RowGraph _graph;
    private readonly Dictionary<RecordNode, int> _layers = [];
    private readonly List<RecordNode> _order = [];
    private readonly Dictionary<string, RecordNode> _byId = new(StringComparer.Ordinal);
    private readonly HashSet<(RecordNode Node, ForeignKeyInfo Relation, LinkDirection Direction)> _followed = [];
    private readonly List<RecordGroup> _groups = [];
    private readonly List<RecordProblem> _problems = [];
    private readonly List<RecordStep> _steps = [];
    private RecordStep[]? _stepsTaken;
    private List<RecordEdge>? _edges;
    private Dictionary<string, RecordEdge>? _edgeById;

    private RecordDiagram(RowTraversal traversal, RelationGraph relations, RowGraph graph, TableRef table, int groupLimit)
    {
        _traversal = traversal;
        _relations = relations;
        _graph = graph;
        Table = table;
        GroupLimit = groupLimit;
        foreach (var row in graph.Of(table)?.Rows ?? [])
        {
            Show(new RecordNode(table, row.Key), 0);
        }

        Start = [.. _order];
    }

    /// <summary>The start rows of <paramref name="table"/>, read again (a row deleted since is left out); nothing expanded yet.</summary>
    public static async Task<RecordDiagram> StartAsync(
        RowTraversal traversal, RelationGraph relations, TableDetails table, IReadOnlyList<RowKey> keys, CancellationToken cancellationToken,
        int groupLimit = DefaultGroupLimit)
    {
        var graph = await traversal.StartAsync(table, keys, cancellationToken);
        return new RecordDiagram(traversal, relations, graph, table.Table.Ref, groupLimit);
    }

    /// <summary>The table of the start rows.</summary>
    public TableRef Table { get; }

    public IReadOnlyList<RecordNode> Start { get; }

    public int GroupLimit { get; }

    /// <summary>The rows shown, in the order they came in (the start rows first).</summary>
    public IReadOnlyList<RecordRow> Rows => [.. _order.Select(n => new RecordRow(n, _layers[n]))];

    /// <summary>Rows shown as one node each, until opened.</summary>
    public IReadOnlyList<RecordGroup> Groups => _groups;

    /// <summary>
    /// What the user did, in order: to build the diagram again (<see cref="ReplayAsync"/>). A snapshot that later steps leave
    /// as it is (review: holders of the list itself saw it grow – the saved state always equalled the next one, and a replay
    /// over it broke when a step came in meanwhile).
    /// </summary>
    public IReadOnlyList<RecordStep> Steps => _stepsTaken ??= [.. _steps];

    /// <summary>
    /// Relationships that could not be followed: too many rows along them (<see cref="RecordProblem.TooMany"/>) or, from the
    /// traversal, a table outside the schema, a missing column, a type that cannot be compared.
    /// </summary>
    public IReadOnlyList<RecordProblem> Problems =>
        [.. _problems, .. _graph.Problems.Select(p => new RecordProblem(p.Relation, null, [], p.Message, TooMany: false))];

    /// <summary>All rows read so far, also those in groups.</summary>
    public int RowCount => _graph.RowCount;

    public bool IsShown(RecordNode node) => _layers.ContainsKey(node);

    /// <summary>The values of a row read so far; null for one the diagram does not hold.</summary>
    public RowData? RowOf(RecordNode node) => _graph.Of(node.Table)?.Find(node.Key);

    /// <summary>Columns and keys of a table rows were read from.</summary>
    public TableDetails? DetailsOf(TableRef table) => _graph.Of(table)?.Details;

    /// <summary>A shown row by its id in the layout (<see cref="RecordNode.Id"/>).</summary>
    public RecordNode? NodeOf(string id) => _byId.GetValueOrDefault(id);

    /// <summary>Links between shown rows (a row referencing itself included), in the order they were found.</summary>
    public IReadOnlyList<RecordEdge> Edges => _edges ??= [.. _graph.Links
        .Select(l => new RecordEdge(l, new RecordNode(l.Relation.From, l.Child), new RecordNode(l.Relation.To, l.Parent)))
        .Where(e => _layers.ContainsKey(e.Child) && _layers.ContainsKey(e.Parent))];

    /// <summary>The link drawn as the edge <paramref name="edgeId"/>; null for the edge of a group.</summary>
    public RecordEdge? EdgeOf(string edgeId) => (_edgeById ??= Edges.ToDictionary(e => e.Id, StringComparer.Ordinal)).GetValueOrDefault(edgeId);

    /// <summary>
    /// The relationships of a shown row on one side not followed yet – the number on its "+" handle. A parent needs the row's
    /// values in the relationship's columns: one with a NULL there references nothing.
    /// </summary>
    public IReadOnlyList<ForeignKeyInfo> Pending(RecordNode node, DiagramSide side)
    {
        if (!_layers.ContainsKey(node) || RowOf(node) is not { } row || DetailsOf(node.Table) is not { } details)
        {
            return [];
        }

        var direction = Direction(side);
        var relations = side == DiagramSide.Parents ? _relations.ParentsOf(node.Table) : _relations.ChildrenOf(node.Table);
        return [.. relations.Where(r => !_followed.Contains((node, r, direction)) && References(details, row, side == DiagramSide.Parents ? r.FromColumns : r.ToColumns))];
    }

    /// <summary>
    /// Follows every pending relationship of <paramref name="nodes"/> on <paramref name="side"/>: one read per relationship
    /// for all the rows (of one table) together. New rows come one column further out, or as a group if too many. The step is
    /// kept also when there is nothing to follow now: relationships of the C# model may come in later, and the reload then
    /// follows them (a tab opened before the model never got its children).
    /// </summary>
    /// <returns>The rows shown now.</returns>
    public async Task<IReadOnlyList<RecordNode>> ExpandAsync(
        IReadOnlyList<RecordNode> nodes, DiagramSide side, IProgress<TraversalProgress>? progress, CancellationToken cancellationToken)
    {
        var distinct = nodes.Distinct().ToList();
        if (distinct.Count == 0)
        {
            return [];
        }

        AddStep(new RecordStep(RecordStepKind.Expand, [.. distinct.Select(n => n.Id)], side));
        return await ExpandSideAsync(distinct, side, progress, cancellationToken);
    }

    /// <summary>Follows one relationship of one row in one direction (the context menu).</summary>
    public async Task<IReadOnlyList<RecordNode>> ExpandAsync(
        RecordNode node, ForeignKeyInfo relation, DiagramSide side, IProgress<TraversalProgress>? progress, CancellationToken cancellationToken)
    {
        if (!Pending(node, side).Contains(relation))
        {
            return [];
        }

        AddStep(new RecordStep(RecordStepKind.Expand, [node.Id], side, RelationGraph.KeyOf(relation)));
        return await FollowAsync([node], relation, Direction(side), progress, cancellationToken);
    }

    /// <summary>Shows the rows of a group in its column; the group goes away.</summary>
    public IReadOnlyList<RecordNode> Open(RecordGroup group)
    {
        if (!_groups.Contains(group))
        {
            return [];
        }

        AddStep(new RecordStep(RecordStepKind.Open, [group.Node.Id], group.Side, RelationGraph.KeyOf(group.Relation)));
        return OpenGroup(group);
    }

    /// <summary>The rows of a group still not shown otherwise (one may have come in along another relationship since).</summary>
    public IReadOnlyList<RecordNode> Members(RecordGroup group) => [.. group.Rows.Where(n => !_layers.ContainsKey(n))];

    /// <summary>
    /// Does <paramref name="steps"/> again on this diagram (of the same start rows, read again): a row that is gone, or a
    /// relationship the schema does not have now, skips its step. Every step is kept all the same – the row may be back after
    /// a rollback, the relationship once the C# model is loaded. Stops at the first error, as an expansion does.
    /// </summary>
    public async Task ReplayAsync(IReadOnlyList<RecordStep> steps, IProgress<TraversalProgress>? progress, CancellationToken cancellationToken)
    {
        foreach (var step in steps)
        {
            AddStep(step);
            var nodes = step.Nodes.Select(NodeOf).OfType<RecordNode>().ToList();
            if (nodes.Count == 0)
            {
                continue;
            }

            var relation = step.Relation is null ? null : _relations.Find(step.Relation);
            switch (step.Kind)
            {
                case RecordStepKind.Expand when step.Relation is null:
                    await ExpandSideAsync(nodes, step.Side, progress, cancellationToken);
                    break;
                case RecordStepKind.Expand when relation is not null && Pending(nodes[0], step.Side).Contains(relation):
                    await FollowAsync([nodes[0]], relation, Direction(step.Side), progress, cancellationToken);
                    break;
                case RecordStepKind.Open when relation is not null
                    && _groups.FirstOrDefault(g => g.Node == nodes[0] && g.Relation == relation && g.Side == step.Side) is { } group:
                    OpenGroup(group);
                    break;
            }
        }
    }

    /// <summary>The nodes and edges for <see cref="DiagramLayout"/>: an edge runs from parent to child; a group hangs on its row.</summary>
    public (IReadOnlyList<LayoutNode> Nodes, IReadOnlyList<LayoutEdge> Edges) ToLayout()
    {
        var groups = _groups.Where(g => Members(g).Count > 0).ToList();
        var nodes = _order.Select(n => new LayoutNode(n.Id, _layers[n])).Concat(groups.Select(g => new LayoutNode(g.Id, g.Layer))).ToList();
        var edges = Edges.Select(e => new LayoutEdge(e.Id, e.Parent.Id, e.Child.Id))
            .Concat(groups.Select(g => g.Side == DiagramSide.Parents
                ? new LayoutEdge(g.Id, g.Id, g.Node.Id)
                : new LayoutEdge(g.Id, g.Node.Id, g.Id)))
            .ToList();
        return (nodes, edges);
    }

    /// <summary>Every pending relationship of <paramref name="nodes"/> on <paramref name="side"/>, one read per relationship.</summary>
    private async Task<IReadOnlyList<RecordNode>> ExpandSideAsync(
        IReadOnlyList<RecordNode> nodes, DiagramSide side, IProgress<TraversalProgress>? progress, CancellationToken cancellationToken)
    {
        var shown = new List<RecordNode>();
        var pending = nodes.SelectMany(n => Pending(n, side).Select(r => (Node: n, Relation: r))).ToList();
        foreach (var along in pending.GroupBy(p => p.Relation))
        {
            shown.AddRange(await FollowAsync([.. along.Select(p => p.Node)], along.Key, Direction(side), progress, cancellationToken));
        }

        return shown;
    }

    private IReadOnlyList<RecordNode> OpenGroup(RecordGroup group)
    {
        _groups.Remove(group);
        var members = Members(group);
        foreach (var node in members)
        {
            Show(node, group.Layer);
        }

        return members;
    }

    private async Task<IReadOnlyList<RecordNode>> FollowAsync(
        IReadOnlyList<RecordNode> nodes, ForeignKeyInfo relation, LinkDirection direction, IProgress<TraversalProgress>? progress,
        CancellationToken cancellationToken)
    {
        var parents = direction == LinkDirection.Parents;
        progress?.Report(new TraversalProgress(parents ? relation.To : relation.From, _graph.RowCount));
        var rows = nodes.Select(RowOf).OfType<RowData>().ToList();
        try
        {
            await _traversal.FollowAsync(_graph, relation, direction, rows, cancellationToken);
        }
        catch (TooManyRowsException ex)
        {
            // The graph is as it was (FollowAsync adds all or nothing); the relationship is not offered again.
            _problems.Add(new RecordProblem(relation, direction, nodes,
                TextFormat.Format(RelationText.RecordTooManyRows, relation.Name, ex.Limit), TooMany: true));
            foreach (var node in nodes)
            {
                _followed.Add((node, relation, direction));
            }

            return [];
        }

        var shown = new List<RecordNode>();
        var side = parents ? DiagramSide.Parents : DiagramSide.Children;
        var links = _graph.LinksAlong(relation);
        foreach (var node in nodes)
        {
            _followed.Add((node, relation, direction));
            var reached = (parents
                    ? links.Where(l => l.Child.Equals(node.Key)).Select(l => new RecordNode(relation.To, l.Parent))
                    : links.Where(l => l.Parent.Equals(node.Key)).Select(l => new RecordNode(relation.From, l.Child)))
                .Where(n => n != node)
                .Distinct()
                .ToList();

            // A child has one parent along the relationship: the row it was reached from.
            if (!parents)
            {
                foreach (var child in reached)
                {
                    _followed.Add((child, relation, LinkDirection.Parents));
                }
            }

            var hidden = reached.Where(n => !_layers.ContainsKey(n)).ToList();
            var outward = _layers[node] + (parents ? -1 : 1);
            if (hidden.Count > GroupLimit)
            {
                _groups.Add(new RecordGroup(node, relation, side, outward, hidden));
                continue;
            }

            foreach (var row in hidden)
            {
                Show(row, outward);
                shown.Add(row);
            }
        }

        _edges = null;
        _edgeById = null;
        return shown;
    }

    private void AddStep(RecordStep step)
    {
        _steps.Add(step);
        _stepsTaken = null;
    }

    private void Show(RecordNode node, int layer)
    {
        if (_layers.TryAdd(node, layer))
        {
            _order.Add(node);
            _byId[node.Id] = node;
            (_edges, _edgeById) = (null, null);
        }
    }

    private static LinkDirection Direction(DiagramSide side) => side == DiagramSide.Parents ? LinkDirection.Parents : LinkDirection.Children;

    /// <summary>The row holds values in all of <paramref name="columns"/> (a missing column counts: following it reports the problem).</summary>
    private static bool References(TableDetails details, RowData row, IReadOnlyList<string> columns) =>
        columns.All(c => details.IndexOf(c) is var i && (i < 0 || row.Values[i] is not null));
}

/// <summary>A row in the record diagram: its table and key.</summary>
public sealed record RecordNode(TableRef Table, RowKey Key)
{
    /// <summary>The node's id in the layout and in saved steps: table and key values as invariant text.</summary>
    public string Id { get; } = $"r:{Table.Owner}.{Table.Name}:{KeyText(Key)}";

    public bool Equals(RecordNode? other) => other is not null && Table == other.Table && Key.Equals(other.Key);

    public override int GetHashCode() => HashCode.Combine(Table, Key);

    /// <summary>
    /// The layout marks its waypoints with U+0001, the separator of <see cref="RowTraversal.KeyText"/>: the values of a key
    /// are joined with U+001F instead.
    /// </summary>
    private static string KeyText(RowKey key) => key switch
    {
        RowKey.PrimaryKey pk => RowTraversal.KeyText([.. pk.Values.Select(v => v ?? "")], trim: false).Replace('\u0001', '\u001F'),
        RowKey.RowId rowId => "rowid:" + rowId.Value,
        _ => "",
    };
}

/// <summary>A shown row and its column.</summary>
public sealed record RecordRow(RecordNode Node, int Layer);

/// <summary>A link between two shown rows.</summary>
public sealed record RecordEdge(RowLink Link, RecordNode Child, RecordNode Parent)
{
    public string Id { get; } = $"{RelationGraph.KeyOf(Link.Relation)}\u001E{Child.Id}\u001E{Parent.Id}";

    public ForeignKeyInfo Relation => Link.Relation;
}

/// <summary>Rows reached from <paramref name="Node"/> along one relationship, shown as one node.</summary>
public sealed record RecordGroup(RecordNode Node, ForeignKeyInfo Relation, DiagramSide Side, int Layer, IReadOnlyList<RecordNode> Rows)
{
    public string Id { get; } = $"g:{Node.Id}\u001E{RelationGraph.KeyOf(Relation)}\u001E{Side}";

    /// <summary>The table of the rows.</summary>
    public TableRef Table => Side == DiagramSide.Parents ? Relation.To : Relation.From;
}

/// <summary>A relationship not followed, and why (a full sentence).</summary>
/// <param name="Direction">Null for a problem the traversal reported (it applies to every row).</param>
/// <param name="From">The rows it was followed from, if <paramref name="TooMany"/>: to show them in a tab instead.</param>
public sealed record RecordProblem(ForeignKeyInfo Relation, LinkDirection? Direction, IReadOnlyList<RecordNode> From, string Message, bool TooMany);

public enum RecordStepKind
{
    /// <summary>Rows expanded on one side: along all its relationships, or one (<see cref="RecordStep.Relation"/>).</summary>
    Expand,

    /// <summary>A group opened: the rows reached from one row along one relationship.</summary>
    Open,
}

/// <summary>
/// Something the user did in the diagram, saved with the tab (values of keys included, like the rest of the workspace file).
/// </summary>
/// <param name="Nodes">Ids of the rows (<see cref="RecordNode.Id"/>).</param>
/// <param name="Relation"><see cref="RelationGraph.KeyOf"/>; null for all relationships of the side.</param>
public sealed record RecordStep(RecordStepKind Kind, IReadOnlyList<string> Nodes, DiagramSide Side, string? Relation = null);
