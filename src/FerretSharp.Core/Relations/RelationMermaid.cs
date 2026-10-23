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
    /// <param name="owner">The connection's schema: tables of other schemas are written with their owner.</param>
    public static string Write(RelationDiagram diagram, string owner)
    {
        var text = new StringBuilder("erDiagram\n");
        var related = new HashSet<TableRef>();
        foreach (var relation in diagram.Relations)
        {
            var line = relation.Source == FkSource.Declared ? "--" : "..";
            text.Append($"    {Name(relation.To, owner)} ||{line}o{{ {Name(relation.From, owner)} : {Quote(relation.Name)}\n");
            related.Add(relation.From);
            related.Add(relation.To);
        }

        // A table without relationships (all of its neighbours in a group) would not appear otherwise.
        foreach (var table in diagram.Tables.Select(t => t.Table).Where(t => !related.Contains(t)))
        {
            text.Append($"    {Name(table, owner)}\n");
        }

        foreach (var group in diagram.Groups)
        {
            var count = diagram.Members(group).Count;
            if (count > 0)
            {
                var comment = group.Side == DiagramSide.Parents
                    ? TextFormat.Plural(count, RelationText.MermaidHiddenParentsOne, RelationText.MermaidHiddenParentsOther, Label(group.Table, owner))
                    : TextFormat.Plural(count, RelationText.MermaidHiddenChildrenOne, RelationText.MermaidHiddenChildrenOther, Label(group.Table, owner));
                text.Append($"    %% {comment}\n");
            }
        }

        return text.ToString();
    }

    /// <summary>The table as people know it here: its name, with the owner if it belongs to another schema.</summary>
    public static string Label(TableRef table, string owner) => table.Owner == owner ? table.Name : $"{table.Owner}.{table.Name}";

    /// <summary>An entity name: plain if Mermaid takes it so (letters, digits, _ and -), otherwise in quotes.</summary>
    private static string Name(TableRef table, string owner)
    {
        var label = Label(table, owner);
        return label.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-') ? label : Quote(label);
    }

    /// <summary>Mermaid has no escape for a double quote inside quotes.</summary>
    private static string Quote(string text) => $"\"{text.Replace('"', '\'')}\"";
}
