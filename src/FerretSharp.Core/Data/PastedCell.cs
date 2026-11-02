namespace FerretSharp.Core.Data;

/// <summary>
/// Text pasted with Ctrl+V on a cell that is not being edited: one value for the focused cell, the marked cells or the
/// selected rows. Ctrl+C on one cell gives the value as it is, Excel adds a line break and quotes a value holding a line
/// break, tab or quote (as <see cref="DelimitedExport"/> does). Several cells – a tab, a line break between values – are
/// not one value: pasting them cell by cell is still to come.
/// </summary>
public static class PastedCell
{
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
