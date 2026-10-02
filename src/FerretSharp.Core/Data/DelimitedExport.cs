using System.Globalization;
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

    private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");

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

    private static string Value(ColumnInfo column, object? value, ExportWarnings warnings) => value switch
    {
        null or DBNull => "",
        decimal d => d.ToString(German),
        BigNumber big => big.Invariant.Replace('.', ','),
        int or long or short => Convert.ToString(value, CultureInfo.InvariantCulture)!,
        double d => d.ToString("R", German),
        float f => f.ToString("R", German),
        DateTimeOffset dto => dto.ToString("dd.MM.yyyy HH:mm:ss.FFFFFFF zzz", CultureInfo.InvariantCulture),
        DateTime dt when ColumnCategories.Of(column) == ColumnCategory.Date => dt.ToString("dd.MM.yyyy HH:mm:ss", CultureInfo.InvariantCulture),
        DateTime dt => dt.ToString("dd.MM.yyyy HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture),
        bool b => b ? "TRUE" : "FALSE",
        byte[] bytes => Convert.ToHexString(bytes),
        LobValue { Preview: { } preview } clob when preview.Length == clob.Length => preview,
        LobValue lob => warnings.Skip(column, lob.Preview is null ? "nur die Länge geladen" : "nur die Vorschau geladen"),
        NotNullMarker => warnings.Skip(column, "Typ wird nicht exportiert"),
        string s => s,
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "",
    };

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

/// <summary>Collects skipped values per column, e.g. "NOTIZ (CLOB): 3 Werte nicht exportiert – nur die Vorschau geladen."</summary>
internal sealed class ExportWarnings
{
    private readonly Dictionary<(string Column, string Type, string Reason), int> _skipped = [];

    /// <returns>Empty text for delimited output.</returns>
    public string Skip(ColumnInfo column, string reason)
    {
        var key = (column.Name, column.DataType, reason);
        _skipped[key] = _skipped.GetValueOrDefault(key) + 1;
        return "";
    }

    public List<string> ToList() => _skipped
        .Select(kv => $"{kv.Key.Column} ({kv.Key.Type}): {(kv.Value == 1 ? "1 Wert" : $"{kv.Value} Werte")} nicht exportiert – {kv.Key.Reason}.")
        .ToList();
}
