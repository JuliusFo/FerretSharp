using System.Text;
using FerretSharp.Core.Resources;
using FerretSharp.Core.Schema;

namespace FerretSharp.Core.Relations;

/// <summary>
/// A relationship diagram as a Mermaid <c>erDiagram</c> for documentation and tickets (WP-46): one line per relationship,
/// parent first, named after the constraint or navigation. A constraint is a solid line, a relationship only of the C#
/// model (or manual, conventional) a dashed one. Groups that were not opened are named in a comment.
/// </summary>
public static class RelationMermaid
{
    /// <param name="label">The name of a table as the diagram shows it (<see cref="SchemaCache.LabelOf"/>).</param>
    public static string Write(RelationDiagram diagram, Func<TableRef, string> label)
    {
        var text = Relations(diagram.Relations, diagram.Tables.Select(t => t.Table), label);
        foreach (var group in diagram.Groups)
        {
            var count = diagram.Members(group).Count;
            if (count > 0)
            {
                var comment = group.Side == DiagramSide.Parents
                    ? TextFormat.Plural(count, RelationText.MermaidHiddenParentsOne, RelationText.MermaidHiddenParentsOther, label(group.Table))
                    : TextFormat.Plural(count, RelationText.MermaidHiddenChildrenOne, RelationText.MermaidHiddenChildrenOther, label(group.Table));
                text.Append($"    %% {MermaidText.Plain(comment)}\n");
            }
        }

        return text.ToString();
    }

    /// <summary>Ways between two tables (WP-74): the relationships of <paramref name="paths"/>, each once, in the order the ways take them.</summary>
    public static string Write(IEnumerable<RelationPath> paths, Func<TableRef, string> label)
    {
        var list = paths.ToList();
        var relations = list.SelectMany(p => p.Steps).Select(s => s.Relation).DistinctBy(RelationGraph.KeyOf);
        return Relations(relations, list.SelectMany(p => p.Tables), label).ToString();
    }

    private static StringBuilder Relations(IEnumerable<ForeignKeyInfo> relations, IEnumerable<TableRef> tables, Func<TableRef, string> label)
    {
        var text = new StringBuilder("erDiagram\n");
        var related = new HashSet<TableRef>();
        foreach (var relation in relations)
        {
            var line = relation.Source == FkSource.Declared ? "--" : "..";
            text.Append($"    {Name(label(relation.To))} ||{line}o{{ {Name(label(relation.From))} : {Quote(relation.Name)}\n");
            related.Add(relation.From);
            related.Add(relation.To);
        }

        // A table without relationships (all of its neighbours in a group) would not appear otherwise.
        foreach (var table in tables.Distinct().Where(t => !related.Contains(t)))
        {
            text.Append($"    {Name(label(table))}\n");
        }

        return text;
    }

    /// <summary>An entity name: plain if Mermaid takes it so (letters, digits, _ and -), otherwise in quotes.</summary>
    private static string Name(string label) => label.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-') ? label : Quote(label);

    /// <summary>On one line (<see cref="MermaidText.Plain"/>); an <c>erDiagram</c> has no escape for a double quote inside quotes.</summary>
    private static string Quote(string text) => $"\"{MermaidText.Plain(text).Replace('"', '\'')}\"";
}
