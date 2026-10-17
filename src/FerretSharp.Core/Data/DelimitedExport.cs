using System.Text;
using FerretSharp.Core.Schema;

namespace FerretSharp.Core.Data;

/// <param name="Warnings">Values that could not be exported completely (LOBs only loaded as preview …).</param>
public sealed record ExportText(string Text, int Rows, IReadOnlyList<string> Warnings);

/// <summary>
/// Rows as delimited text with a header line: <c>;</c> for CSV files (German Excel), tab for the clipboard (pasting
/// into Excel splits on tabs). Numbers and dates in German notation without grouping, NULL as empty field.
/// </summary>
public static class DelimitedExport
{
    public const char CsvSeparator = ';';
    public const char ClipboardSeparator = '\t';

    public static ExportText Build(TableDetails table, IReadOnlyList<RowData> rows, char separator)
    {
        var warnings = new ExportWarnings();
        var text = new StringBuilder();
        AppendLine(text, table.Columns.Select(c => c.Name), separator);
        foreach (var row in rows)
        {
            AppendLine(text, table.Columns.Select((c, i) => Value(c, row.Values[i], warnings)), separator);
        }

        return new ExportText(text.ToString(), rows.Count, warnings.ToList());
    }

    /// <summary>
    /// One cell for the clipboard (Ctrl+C, "Copy value"): same notation as the table export and the full value –
    /// unlike the grid's display text, which shortens long texts, shows line breaks as ⏎ and groups thousands.
    /// NULL is an empty text; a LOB that was only loaded as preview is left out with a warning.
    /// </summary>
    public static ExportText CellText(ColumnInfo column, object? value)
    {
        var warnings = new ExportWarnings();
        var text = Value(column, value, warnings);
        return new ExportText(text, 1, warnings.ToList());
    }

    private static string Value(ColumnInfo column, object? value, ExportWarnings warnings) =>
        ExportNotation.Text(column, value, warnings) ?? "";

    private static void AppendLine(StringBuilder text, IEnumerable<string> fields, char separator)
    {
        var first = true;
        foreach (var field in fields)
        {
            if (!first)
            {
                text.Append(separator);
            }

            first = false;
            if (field.IndexOfAny([separator, '"', '\r', '\n']) >= 0)
            {
                text.Append('"').Append(field.Replace("\"", "\"\"", StringComparison.Ordinal)).Append('"');
            }
            else
            {
                text.Append(field);
            }
        }

        text.Append("\r\n");
    }
}

