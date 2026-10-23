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
        var text = new StringBuilder("erDiagram\n");
        var related = new HashSet<TableRef>();
        foreach (var relation in diagram.Relations)
        {
            var line = relation.Source == FkSource.Declared ? "--" : "..";
            text.Append($"    {Name(label(relation.To))} ||{line}o{{ {Name(label(relation.From))} : {Quote(relation.Name)}\n");
            related.Add(relation.From);
            related.Add(relation.To);
        }

        // A table without relationships (all of its neighbours in a group) would not appear otherwise.
        foreach (var table in diagram.Tables.Select(t => t.Table).Where(t => !related.Contains(t)))
        {
            text.Append($"    {Name(label(table))}\n");
        }

        foreach (var group in diagram.Groups)
        {
            var count = diagram.Members(group).Count;
            if (count > 0)
            {
                var comment = group.Side == DiagramSide.Parents
                    ? TextFormat.Plural(count, RelationText.MermaidHiddenParentsOne, RelationText.MermaidHiddenParentsOther, label(group.Table))
                    : TextFormat.Plural(count, RelationText.MermaidHiddenChildrenOne, RelationText.MermaidHiddenChildrenOther, label(group.Table));
                text.Append($"    %% {comment}\n");
            }
        }

        return text.ToString();
    }

    /// <summary>An entity name: plain if Mermaid takes it so (letters, digits, _ and -), otherwise in quotes.</summary>
    private static string Name(string label) => label.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-') ? label : Quote(label);

    /// <summary>Mermaid has no escape for a double quote inside quotes.</summary>
    private static string Quote(string text) => $"\"{text.Replace('"', '\'')}\"";
}
