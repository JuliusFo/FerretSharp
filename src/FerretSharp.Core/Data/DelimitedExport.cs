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
        AppendHeader(text, table, separator);
        AppendRows(text, table, rows, separator, warnings);
        return new ExportText(text.ToString(), rows.Count, warnings.ToList());
    }

    /// <summary>
    /// A CSV file written block by block (WP-54): UTF-8 with byte order mark (Excel needs it to read UTF-8), separator
    /// <see cref="CsvSeparator"/>, the notation of <see cref="Build"/>. The stream stays open.
    /// </summary>
    public static IExportWriter CreateWriter(Stream stream, TableDetails table) => new Writer(stream, table, CsvSeparator);

    private static void AppendHeader(StringBuilder text, TableDetails table, char separator) =>
        AppendLine(text, table.Columns.Select(c => c.Name), separator);

    private static void AppendRows(StringBuilder text, TableDetails table, IEnumerable<RowData> rows, char separator, ExportWarnings warnings)
    {
        foreach (var row in rows)
        {
            AppendLine(text, table.Columns.Select((c, i) => Value(c, row.Values[i], warnings)), separator);
        }
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

    private sealed class Writer(Stream stream, TableDetails table, char separator) : IExportWriter
    {
        private readonly StreamWriter _file = new(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), bufferSize: 1 << 16, leaveOpen: true);
        private readonly ExportWarnings _warnings = new();
        private readonly StringBuilder _text = new();
        private bool _started;

        public long Rows { get; private set; }

        public async ValueTask<bool> WriteAsync(IReadOnlyList<RowData> rows, CancellationToken cancellationToken)
        {
            Start();
            AppendRows(_text, table, rows, separator, _warnings);
            await FlushTextAsync(cancellationToken);
            Rows += rows.Count;
            return true;
        }

        public async ValueTask<ExportSummary> FinishAsync(CancellationToken cancellationToken)
        {
            Start(); // no rows: the header alone
            await FlushTextAsync(cancellationToken);
            await _file.FlushAsync(cancellationToken);
            return new ExportSummary(Rows, _warnings.ToList());
        }

        public ValueTask DisposeAsync() => _file.DisposeAsync();

        private void Start()
        {
            if (!_started)
            {
                _started = true;
                AppendHeader(_text, table, separator);
            }
        }

        private async Task FlushTextAsync(CancellationToken cancellationToken)
        {
            await _file.WriteAsync(_text, cancellationToken);
            _text.Clear();
        }
    }
}

