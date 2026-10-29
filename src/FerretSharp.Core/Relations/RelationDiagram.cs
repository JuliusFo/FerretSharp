using FerretSharp.Core.Schema;

namespace FerretSharp.Core.Relations;

/// <summary>
/// The relationship diagram of a table (WP-46): the table in the middle, the tables it references to the left (parents),
/// those that reference it to the right (children), one or two levels deep; each shown table can be expanded on either
/// side. A side with more new neighbours than <see cref="DiagramOptions.GroupLimit"/> shows them as one group node
/// ("45 tables") until it is opened. Only the schema's relationships, no rows; not thread-safe (the UI thread changes it).
/// </summary>
public sealed class RelationDiagram
{
    /// <summary>Levels the view offers (review 3.54.0, B1: the 2 stood in the view and its state).</summary>
    public const int MaxDepth = 2;

    private readonly RelationGraph _graph;
    private readonly DiagramOptions _options;
    private readonly Dictionary<TableRef, int> _layers = [];
    private readonly List<TableRef> _order = [];
    private readonly List<DiagramGroup> _groups = [];
    private List<ForeignKeyInfo>? _relations;
    private Dictionary<string, ForeignKeyInfo>? _byKey;

    public RelationDiagram(RelationGraph graph, TableRef focus, DiagramOptions options)
    {
        _graph = new RelationGraph(graph.Relations.Where(r => options.Sources.Contains(r.Source)));
        _options = options;
        Focus = focus;
        Show(focus, 0);
        var parents = Expand(focus, DiagramSide.Parents);
        var children = Expand(focus, DiagramSide.Children);
        if (options.Depth > 1)
        {
            foreach (var table in parents)
            {
                Expand(table, DiagramSide.Parents);
            }

            foreach (var table in children)
            {
                Expand(table, DiagramSide.Children);
            }
        }
    }

    public TableRef Focus { get; }

    public DiagramOptions Options => _options;

    /// <summary>The tables shown, in the order they came in (the middle first).</summary>
    public IReadOnlyList<DiagramTable> Tables => [.. _order.Select(t => new DiagramTable(t, _layers[t]))];

    /// <summary>Neighbours shown as one node each, until opened.</summary>
    public IReadOnlyList<DiagramGroup> Groups => _groups;

    /// <summary>
    /// Relationships between shown tables (self references included), in the order of the graph. Kept until a table is
    /// shown: the view asks on every render, and the graph holds the whole schema's relationships (review).
    /// </summary>
    public IReadOnlyList<ForeignKeyInfo> Relations => _relations ??= [.. _graph.Relations.Where(r => _layers.ContainsKey(r.From) && _layers.ContainsKey(r.To))];

    /// <summary>The shown relationship drawn as the edge <paramref name="edgeId"/> (<see cref="ToLayout"/>); null for the edge of a group.</summary>
    public ForeignKeyInfo? RelationOf(string edgeId)
    {
        if (_byKey is null)
        {
            _byKey = new Dictionary<string, ForeignKeyInfo>(StringComparer.Ordinal);
            foreach (var relation in Relations)
            {
                _byKey.TryAdd(RelationGraph.KeyOf(relation), relation);
            }
        }

        return _byKey.GetValueOrDefault(edgeId);
    }

    public bool IsShown(TableRef table) => _layers.ContainsKey(table);

    /// <summary>Neighbours of a shown table on one side that are neither shown nor in a group – the number on its "+" handle.</summary>
    public int HiddenCount(TableRef table, DiagramSide side) => Hidden(table, side).Count;

    /// <summary>The relationships to the hidden neighbours of a shown table on one side – their kinds colour its "+" handle.</summary>
    public IReadOnlyList<ForeignKeyInfo> HiddenRelations(TableRef table, DiagramSide side)
    {
        var hidden = Hidden(table, side).ToHashSet();
        return side == DiagramSide.Parents
            ? [.. _graph.ParentsOf(table).Where(r => hidden.Contains(r.To))]
            : [.. _graph.ChildrenOf(table).Where(r => hidden.Contains(r.From))];
    }

    /// <summary>Shows the hidden neighbours of <paramref name="table"/> on <paramref name="side"/> one column further out – or as a group, if too many.</summary>
    /// <returns>The tables shown now (none if they went into a group).</returns>
    public IReadOnlyList<TableRef> Expand(TableRef table, DiagramSide side)
    {
        if (!_layers.TryGetValue(table, out var layer))
        {
            return [];
        }

        var hidden = Hidden(table, side);
        if (hidden.Count == 0)
        {
            return [];
        }

        var outward = layer + (side == DiagramSide.Parents ? -1 : 1);
        if (hidden.Count > _options.GroupLimit)
        {
            _groups.Add(new DiagramGroup(table, side, outward, hidden));
            return [];
        }

        foreach (var neighbour in hidden)
        {
            Show(neighbour, outward);
        }

        return hidden;
    }

    /// <summary>Shows the tables of a group in its column; the group goes away.</summary>
    public IReadOnlyList<TableRef> Open(DiagramGroup group)
    {
        if (!_groups.Remove(group))
        {
            return [];
        }

        var tables = Members(group);
        foreach (var table in tables)
        {
            Show(table, group.Layer);
        }

        return tables;
    }

    /// <summary>The tables of a group still not shown otherwise (one may have been shown by expanding another table).</summary>
    public IReadOnlyList<TableRef> Members(DiagramGroup group) => [.. group.Tables.Where(t => !_layers.ContainsKey(t))];

    /// <summary>The nodes and edges for <see cref="DiagramLayout"/>: an edge runs from parent to child; a group hangs on its table.</summary>
    public (IReadOnlyList<LayoutNode> Nodes, IReadOnlyList<LayoutEdge> Edges) ToLayout()
    {
        var groups = _groups.Where(g => Members(g).Count > 0).ToList();
        var nodes = _order.Select(t => new LayoutNode(IdOf(t), _layers[t])).Concat(groups.Select(g => new LayoutNode(g.Id, g.Layer))).ToList();
        var edges = Relations.Select(r => new LayoutEdge(RelationGraph.KeyOf(r), IdOf(r.To), IdOf(r.From)))
            .Concat(groups.Select(g => g.Side == DiagramSide.Parents
                ? new LayoutEdge(g.Id, g.Id, IdOf(g.Table))
                : new LayoutEdge(g.Id, IdOf(g.Table), g.Id)))
            .ToList();
        return (nodes, edges);
    }

    /// <summary>Node id of a table in the layout.</summary>
    public static string IdOf(TableRef table) => $"t:{table.Owner}.{table.Name}";

    private void Show(TableRef table, int layer)
    {
        if (_layers.TryAdd(table, layer))
        {
            _order.Add(table);
            (_relations, _byKey) = (null, null);
        }
    }

    private List<TableRef> Hidden(TableRef table, DiagramSide side)
    {
        var relations = side == DiagramSide.Parents ? _graph.ParentsOf(table).Select(r => r.To) : _graph.ChildrenOf(table).Select(r => r.From);
        var grouped = _groups.Where(g => g.Table == table && g.Side == side).SelectMany(g => g.Tables).ToHashSet();
        return [.. relations
            .Where(t => t != table && !_layers.ContainsKey(t) && !grouped.Contains(t))
            .Distinct()
            .Order(RelationGraph.TableComparer.Instance)];
    }
}

/// <summary>Parents (referenced tables, to the left) or children (referencing tables, to the right).</summary>
public enum DiagramSide
{
    Parents,
    Children,
}

/// <param name="Depth">1: the neighbours; 2: also their parents' parents and children's children.</param>
/// <param name="Sources">Kinds of relationship shown.</param>
/// <param name="GroupLimit">More new neighbours on one side than this become one group node.</param>
public sealed record DiagramOptions(int Depth, IReadOnlySet<FkSource> Sources, int GroupLimit = DiagramOptions.DefaultGroupLimit)
{
    public const int DefaultGroupLimit = 12;

    public static readonly IReadOnlySet<FkSource> AllSources = Enum.GetValues<FkSource>().ToHashSet();

    public static readonly DiagramOptions Default = new(1, AllSources);
}

/// <summary>A shown table and its column.</summary>
public sealed record DiagramTable(TableRef Table, int Layer);

/// <summary>Neighbours of <paramref name="Table"/> on one side, shown as one node.</summary>
public sealed record DiagramGroup(TableRef Table, DiagramSide Side, int Layer, IReadOnlyList<TableRef> Tables)
{
    public string Id => $"g:{Table.Owner}.{Table.Name}:{Side}";
}
