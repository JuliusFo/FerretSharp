using System.Text;
using FerretSharp.Core.Schema;

namespace FerretSharp.Core.Relations;

/// <summary>
/// A record diagram as a Mermaid <c>flowchart LR</c> (WP-60): one node per shown row, labelled as the diagram shows it (with
/// its values), one edge per link from parent to child, named after the relationship. A constraint is a solid line, a
/// relationship only of the C# model (or manual, conventional) a dotted one. A group that was not opened is one node
/// ("POSITION ×200"). An <c>erDiagram</c> (<see cref="RelationMermaid"/>) has no rows, only entities.
/// </summary>
public static class RecordMermaid
{
    /// <param name="row">The label of a row as the diagram shows it (table, key, display value).</param>
    /// <param name="table">The name of a table as the diagram shows it (<see cref="SchemaCache.LabelOf"/>).</param>
    public static string Write(RecordDiagram diagram, Func<RecordNode, string> row, Func<TableRef, string> table)
    {
        var text = new StringBuilder("flowchart LR\n");
        var ids = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var shown in diagram.Rows)
        {
            ids[shown.Node.Id] = $"r{ids.Count + 1}";
            text.Append($"    {ids[shown.Node.Id]}[{Quote(row(shown.Node))}]\n");
        }

        var groups = diagram.Groups.Select(g => (Group: g, Count: diagram.Members(g).Count)).Where(g => g.Count > 0).ToList();
        foreach (var (group, count) in groups)
        {
            ids[group.Id] = $"g{ids.Count + 1}";
            text.Append($"    {ids[group.Id]}[{Quote($"{table(group.Table)} ×{count}")}]\n");
        }

        foreach (var edge in diagram.Edges)
        {
            text.Append($"    {ids[edge.Parent.Id]} {Arrow(edge.Relation)}|{Quote(edge.Relation.Name)}| {ids[edge.Child.Id]}\n");
        }

        foreach (var (group, _) in groups)
        {
            var (from, to) = group.Side == DiagramSide.Parents ? (ids[group.Id], ids[group.Node.Id]) : (ids[group.Node.Id], ids[group.Id]);
            text.Append($"    {from} {Arrow(group.Relation)}|{Quote(group.Relation.Name)}| {to}\n");
        }

        return text.ToString();
    }

    private static string Arrow(ForeignKeyInfo relation) => relation.Source == FkSource.Declared ? "-->" : "-.->";

    /// <summary>Mermaid text in quotes; a double quote inside is its entity <c>#quot;</c>.</summary>
    private static string Quote(string text) => $"\"{text.Replace("\"", "#quot;", StringComparison.Ordinal).Replace('\n', ' ').Replace('\r', ' ')}\"";
}
