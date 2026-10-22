using System.Text;

namespace FerretSharp.Core.Query;

/// <summary>
/// Text pasted into the value of a list filter ("in list", "not in list"): the values of a column copied from the grid –
/// one per line, as "Copy values" or Ctrl+C on marked cells give them, or from Excel – or an IN list for SQL
/// (<c>'A', 'B'</c>, as "Copy as IN list" gives it) become the filter's values separated by
/// <see cref="FilterCondition.ListSeparator"/>. Found in the E2E of WP-58: the IN list ended up as one value with quotes,
/// the lines ran together in the one-line field.
/// </summary>
public static class PastedList
{
    /// <summary>
    /// The filter text for pasted text; null if it is neither several lines nor an IN list – a single value, or values
    /// already separated by <see cref="FilterCondition.ListSeparator"/> – or a value holds the separator: then the text is
    /// pasted as it is.
    /// </summary>
    public static string? ToFilterValue(string text)
    {
        var values = InList(text) ?? Lines(text);
        return values is null || values.Any(v => v.Contains(FilterCondition.ListSeparator, StringComparison.Ordinal))
            ? null
            : string.Join(FilterCondition.ListSeparator + " ", values);
    }

    /// <summary>
    /// Oracle literals separated by commas, optionally in parentheses: <c>'A'</c> (quotes doubled inside), <c>N'A'</c>,
    /// numbers, <c>TO_DATE('…', '…')</c> and the like (the first literal is the value), <c>HEXTORAW('…')</c>; NULL is left
    /// out. Null if the text is anything else, or a single bare number (that is just a value). Without a quoted literal a
    /// comma counts only with a space after it: <c>1,5</c> is a German decimal number, <c>1, 5</c> a list.
    /// </summary>
    private static List<string>? InList(string text)
    {
        var s = text.Trim();
        if (s.StartsWith('(') && s.EndsWith(')'))
        {
            s = s[1..^1];
        }

        var values = new List<string>();
        var quoted = false;
        var tightComma = false;
        var i = 0;
        while (true)
        {
            SkipSpace(s, ref i);
            if (i >= s.Length)
            {
                return null; // empty, or a trailing comma
            }

            if (Word(s, i, "NULL"))
            {
                i += 4;
            }
            else if (Literal(s, ref i) is { } literal)
            {
                values.Add(literal);
                quoted = true;
            }
            else if (Function(s, ref i) is { } inner)
            {
                values.Add(inner);
                quoted = true;
            }
            else if (Number(s, ref i) is { } number)
            {
                values.Add(number);
            }
            else
            {
                return null;
            }

            SkipSpace(s, ref i);
            if (i >= s.Length)
            {
                break;
            }

            if (s[i] != ',')
            {
                return null;
            }

            i++;
            tightComma |= i < s.Length && !char.IsWhiteSpace(s[i]);
        }

        return quoted || (values.Count > 1 && !tightComma) ? values : null;
    }

    /// <summary>
    /// Non-empty lines, each trimmed, a cell in quotes unquoted like Excel quotes it (<c>"a ""b"""</c>); null for fewer than
    /// two, or for several columns (tabs) – a range of cells is no list of values.
    /// </summary>
    private static List<string>? Lines(string text)
    {
        if (text.Contains('\t', StringComparison.Ordinal))
        {
            return null;
        }

        var lines = text.Split(['\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Length > 1 && l[0] == '"' && l[^1] == '"' ? l[1..^1].Replace("\"\"", "\"", StringComparison.Ordinal) : l)
            .ToList();
        return lines.Count > 1 ? lines : null;
    }

    private static void SkipSpace(string s, ref int i)
    {
        while (i < s.Length && char.IsWhiteSpace(s[i]))
        {
            i++;
        }
    }

    private static bool Word(string s, int i, string word) =>
        string.Compare(s, i, word, 0, word.Length, StringComparison.OrdinalIgnoreCase) == 0
        && (i + word.Length >= s.Length || !char.IsLetterOrDigit(s[i + word.Length]) && s[i + word.Length] != '_');

    /// <summary><c>'…'</c> or <c>N'…'</c> at <paramref name="i"/>, quotes doubled inside; null if there is none.</summary>
    private static string? Literal(string s, ref int i)
    {
        var at = i;
        if (at < s.Length && s[at] is 'N' or 'n' && at + 1 < s.Length && s[at + 1] == '\'')
        {
            at++;
        }

        if (at >= s.Length || s[at] != '\'')
        {
            return null;
        }

        var value = new StringBuilder();
        for (at++; at < s.Length; at++)
        {
            if (s[at] != '\'')
            {
                value.Append(s[at]);
            }
            else if (at + 1 < s.Length && s[at + 1] == '\'')
            {
                value.Append('\'');
                at++;
            }
            else
            {
                i = at + 1;
                return value.ToString();
            }
        }

        return null; // not closed
    }

    /// <summary><c>NAME('…'[, '…'])</c>: the first literal; null if there is none at <paramref name="i"/>.</summary>
    private static string? Function(string s, ref int i)
    {
        var at = i;
        while (at < s.Length && (char.IsLetter(s[at]) || s[at] == '_'))
        {
            at++;
        }

        if (at == i)
        {
            return null;
        }

        SkipSpace(s, ref at);
        if (at >= s.Length || s[at] != '(')
        {
            return null;
        }

        at++;
        SkipSpace(s, ref at);
        if (Literal(s, ref at) is not { } value)
        {
            return null;
        }

        while (true)
        {
            SkipSpace(s, ref at);
            if (at >= s.Length)
            {
                return null;
            }

            if (s[at] == ')')
            {
                i = at + 1;
                return value;
            }

            if (s[at] != ',')
            {
                return null;
            }

            at++;
            SkipSpace(s, ref at);
            if (Literal(s, ref at) is null)
            {
                return null;
            }
        }
    }

    /// <summary>A number like <c>-12.5</c> or <c>1E3</c> at <paramref name="i"/>; null if there is none.</summary>
    private static string? Number(string s, ref int i)
    {
        var at = i;
        if (at < s.Length && s[at] is '-' or '+')
        {
            at++;
        }

        var digits = at;
        while (at < s.Length && (char.IsAsciiDigit(s[at]) || s[at] == '.'))
        {
            at++;
        }

        if (at == digits || !s[digits..at].Any(char.IsAsciiDigit))
        {
            return null;
        }

        if (at < s.Length && s[at] is 'E' or 'e')
        {
            var exponent = at + 1;
            if (exponent < s.Length && s[exponent] is '-' or '+')
            {
                exponent++;
            }

            if (exponent < s.Length && char.IsAsciiDigit(s[exponent]))
            {
                at = exponent;
                while (at < s.Length && char.IsAsciiDigit(s[at]))
                {
                    at++;
                }
            }
        }

        var value = s[i..at];
        i = at;
        return value;
    }
}
