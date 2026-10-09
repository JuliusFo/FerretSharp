using System.Text;
using FerretSharp.Core.Resources;

namespace FerretSharp.Core.Compare;

/// <summary>
/// The comparison matrix as a Markdown table for the clipboard (WP-20, first stage of the export): one line per object
/// and per differing column, constraint or index below it.
/// </summary>
public static class CompareExport
{
    /// <param name="labels">One per side, e.g. <c>ERP Dev · ERP</c>.</param>
    /// <param name="onlyDifferences">Leave out objects and children without differences.</param>
    /// <param name="kinds">Kinds of rows to include; null = all.</param>
    public static string Markdown(SchemaComparison comparison, IReadOnlyList<string> labels, bool onlyDifferences, IReadOnlyCollection<CompareKind>? kinds = null)
    {
        var text = new StringBuilder();
        text.Append("| ").Append(Escape(CompareText.ExportObject)).Append(" | ").AppendJoin(" | ", labels.Select(Escape)).Append(" |\n");
        text.Append("|---|").Append(string.Concat(Enumerable.Repeat("---|", labels.Count))).Append('\n');
        foreach (var row in comparison.Objects.Where(r => Shown(r, onlyDifferences, kinds)))
        {
            Line(text, row, $"**{Escape(row.Name)}**", comparison.Options.Reference);
            foreach (var child in row.Children.Where(c => (!onlyDifferences || c.Differs) && (kinds is null || kinds.Contains(c.Kind))))
            {
                Line(text, child, $"{Escape(row.Name)} · {Escape(child.Name)}", comparison.Options.Reference);
            }
        }

        return text.ToString();
    }

    /// <summary>Whether an object row appears at all (itself or one of its children matches the filters).</summary>
    public static bool Shown(CompareRow row, bool onlyDifferences, IReadOnlyCollection<CompareKind>? kinds) =>
        (!onlyDifferences || row.HasDifferences)
        && (kinds is null || kinds.Contains(row.Kind) || row.Children.Any(c => kinds.Contains(c.Kind) && (!onlyDifferences || c.Differs)));

    private static void Line(StringBuilder text, CompareRow row, string name, int? reference)
    {
        text.Append("| ").Append(name).Append(" | ");
        text.AppendJoin(" | ", row.Cells.Select((cell, i) => Cell(cell, i == reference))).Append(" |\n");
    }

    /// <summary>Missing as a dash, differences bold, another letter case with the side's name, the reference marked.</summary>
    private static string Cell(CompareCell cell, bool isReference)
    {
        var text = cell.State switch
        {
            CellState.Missing => "–",
            CellState.Different => $"**{Escape(cell.Definition ?? "")}**",
            CellState.OtherCase => TextFormat.Format(CompareText.ExportOtherCase, Escape(cell.Definition ?? ""), Escape(cell.Name ?? "")),
            _ => Escape(cell.Definition ?? ""),
        };
        return isReference && cell.State != CellState.Missing ? TextFormat.Format(CompareText.ExportReference, text) : text;
    }

    private static string Escape(string text) => text.Replace("|", "\\|", StringComparison.Ordinal).ReplaceLineEndings(" ");
}
