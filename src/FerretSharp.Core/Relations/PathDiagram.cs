using FerretSharp.Core.Schema;

namespace FerretSharp.Core.Relations;

/// <summary>
/// The ways found between two tables as one diagram (WP-74): the start in the leftmost column, the target in the
/// rightmost, every table between in the column of its shortest distance from the start along a way. Edges run from
/// parent to child as in the relationship diagram (WP-46), so the dot still marks the referencing end – here a parent
/// may stand right of its child.
/// </summary>
public sealed class PathDiagram
{
    private readonly Dictionary<TableRef, int> _layers = [];
    private readonly Dictionary<string, ForeignKeyInfo> _relations = new(StringComparer.Ordinal);

    public PathDiagram(TableRef start, TableRef target, IReadOnlyList<RelationPath> paths)
    {
        Start = start;
        Target = target;
        Paths = paths;
        _layers[start] = 0;
        foreach (var path in paths)
        {
            var at = 0;
            foreach (var step in path.Steps)
            {
                at++;
                if (step.To != target && (!_layers.TryGetValue(step.To, out var layer) || at < layer))
                {
                    _layers[step.To] = at;
                }

                _relations.TryAdd(RelationGraph.KeyOf(step.Relation), step.Relation);
            }
        }

        var right = paths.Count == 0 ? 1 : Math.Max(paths.Max(p => p.Length), _layers.Values.DefaultIfEmpty(0).Max() + 1);
        _layers[target] = right;
    }

    public TableRef Start { get; }

    public TableRef Target { get; }

    public IReadOnlyList<RelationPath> Paths { get; }

    /// <summary>The tables shown with their column; the start first.</summary>
    public IReadOnlyList<DiagramTable> Tables =>
        [.. _layers.OrderBy(l => l.Value).ThenBy(l => l.Key, RelationGraph.TableComparer.Instance).Select(l => new DiagramTable(l.Key, l.Value))];

    /// <summary>The relationships of all ways, each once.</summary>
    public IReadOnlyCollection<ForeignKeyInfo> Relations => _relations.Values;

    /// <summary>The relationship drawn as edge <paramref name="edgeId"/>.</summary>
    public ForeignKeyInfo? RelationOf(string edgeId) => _relations.GetValueOrDefault(edgeId);

    public (IReadOnlyList<LayoutNode> Nodes, IReadOnlyList<LayoutEdge> Edges) ToLayout()
    {
        var nodes = Tables.Select(t => new LayoutNode(RelationDiagram.IdOf(t.Table), t.Layer)).ToList();
        var edges = _relations.Values
            .OrderBy(r => RelationGraph.KeyOf(r), StringComparer.Ordinal)
            .Select(r => new LayoutEdge(RelationGraph.KeyOf(r), RelationDiagram.IdOf(r.To), RelationDiagram.IdOf(r.From)))
            .ToList();
        return (nodes, edges);
    }
}
