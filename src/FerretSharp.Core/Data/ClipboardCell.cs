using FerretSharp.Core.Schema;

namespace FerretSharp.Core.Data;

/// <summary>
/// One cell on the clipboard, both ways. Ctrl+C on one cell gives the value as Excel does: quoted (inner quotes doubled)
/// if it holds a line break or tab, so that pasting it – here or in Excel – gives it back whole. Ctrl+V on a cell that
/// is not being edited takes one value: the line break Excel adds to a single cell and its quotes go. Several cells – a
/// tab, a line break between values – are not one value: pasting them cell by cell is still to come.
/// </summary>
public static class ClipboardCell
{
    /// <summary>
    /// The text Ctrl+C puts on the clipboard for one cell: the full value of <see cref="DelimitedExport.CellText"/>, in
    /// quotes where <see cref="ValueOf"/> would not give it back as it is (a line break or tab, or quotes it would take
    /// for Excel's).
    /// </summary>
    public static ExportText Copied(ColumnInfo column, object? value)
    {
        var cell = DelimitedExport.CellText(column, value);
        return cell with { Text = Quoted(cell.Text) };
    }

    /// <summary>The value of pasted text; null if it holds several cells or lines.</summary>
    public static string? ValueOf(string text)
    {
        var value = text.EndsWith("\r\n", StringComparison.Ordinal) ? text[..^2]
            : text.EndsWith('\n') ? text[..^1]
            : text;
        if (Unquoted(value) is { } unquoted)
        {
            return unquoted;
        }

        return value.AsSpan().IndexOfAny('\t', '\r', '\n') >= 0 ? null : value;
    }

    private static string Quoted(string value) =>
        value.AsSpan().IndexOfAny('\t', '\r', '\n') >= 0 || Unquoted(value) is not null
            ? $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\""
            : value;

    /// <summary>
    /// A value Excel quoted: in quotes, every quote inside doubled, and something inside that needed the quotes. Null
    /// otherwise – <c>"abc"</c> stays as it is, quotes and all.
    /// </summary>
    private static string? Unquoted(string value)
    {
        if (value.Length < 2 || value[0] != '"' || value[^1] != '"')
        {
            return null;
        }

        var inner = value[1..^1];
        var doubled = false;
        for (var i = 0; i < inner.Length; i++)
        {
            if (inner[i] != '"')
            {
                continue;
            }

            if (i + 1 >= inner.Length || inner[i + 1] != '"')
            {
                return null;
            }

            doubled = true;
            i++;
        }

        return doubled || inner.AsSpan().IndexOfAny('\t', '\r', '\n') >= 0
            ? inner.Replace("\"\"", "\"", StringComparison.Ordinal)
            : null;
    }
}
