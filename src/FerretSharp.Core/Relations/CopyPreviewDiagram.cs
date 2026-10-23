using FerretSharp.Core.Schema;

namespace FerretSharp.Core.Relations;

/// <summary>
/// The preview of a copy (WP-42) as a diagram (WP-60): one node per table of the plan, an edge for each relationship that
/// links copied rows, from parent to child. A table stands one column right of the furthest of its parents, so the columns
/// read in the order the copy inserts; in a cycle between tables the layering stops after as many passes as there are
/// tables. Laid out by <see cref="DiagramLayout"/>, node ids as in the relationship diagram (<see cref="RelationDiagram.IdOf"/>).
/// </summary>
public static class CopyPreviewDiagram
{
    public static (IReadOnlyList<LayoutNode> Nodes, IReadOnlyList<LayoutEdge> Edges) ToLayout(SubgraphCopyPlan plan, RowGraph graph)
    {
        var tables = plan.Tables.Select(t => t.Table).ToHashSet();
        var relations = graph.RelationsLinked
            .Where(r => r.From != r.To && tables.Contains(r.From) && tables.Contains(r.To))
            .OrderBy(RelationGraph.KeyOf, StringComparer.Ordinal)
            .ToList();

        var layers = plan.Tables.ToDictionary(t => t.Table, _ => 0);
        for (var pass = 0; pass < layers.Count; pass++)
        {
            var moved = false;
            foreach (var relation in relations)
            {
                if (layers[relation.From] <= layers[relation.To])
                {
                    layers[relation.From] = layers[relation.To] + 1;
                    moved = true;
                }
            }

            if (!moved)
            {
                break;
            }
        }

        return (
            [.. plan.Tables.Select(t => new LayoutNode(RelationDiagram.IdOf(t.Table), layers[t.Table]))],
            [.. relations.Select(r => new LayoutEdge(RelationGraph.KeyOf(r), RelationDiagram.IdOf(r.To), RelationDiagram.IdOf(r.From)))]);
    }
}
