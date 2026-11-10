using System.Text;
using FerretSharp.Core.Query;

namespace FerretSharp.Core.Ddl;

/// <summary>
/// The query of a view or materialized view as the schema comparison compares it (WP-82): Oracle keeps the text as it was
/// written, so two equal views differ in spaces, comments, letter case and in the schema they name.
/// </summary>
internal static class ViewText
{
    /// <summary>
    /// <paramref name="text"/> as one line of tokens: without comments, one space between tokens (none around a dot, before , and ) or after (),
    /// words in upper case, a quoted name that needs no quotes (<c>"KUNDE"</c>) without them, and without the prefix of the
    /// own schema (<c>ERP.KUNDE</c>, <c>"ERP"."KUNDE"</c> → <c>KUNDE</c>; decision of the user, 2026-11-10 – a table alias
    /// named like the schema goes as well). String literals and other quoted names stay exactly as written.
    /// </summary>
    /// <param name="owner">The schema of the view, exactly as in the dictionary.</param>
    public static string Normalize(string text, string owner)
    {
        ArgumentNullException.ThrowIfNull(text);
        var tokens = Significant(text);
        var result = new StringBuilder(text.Length);
        string? previous = null;
        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (previous != "." && IsName(token, owner) && i + 1 < tokens.Count && tokens[i + 1].IsSymbol("."))
            {
                i++; // the schema and its dot
                continue;
            }

            var value = token.Kind switch
            {
                SqlTokenKind.Word => token.Value.ToUpperInvariant(),
                SqlTokenKind.QuotedIdentifier => NeedsQuotes(token.Value) ? "\"" + token.Value + "\"" : token.Value,
                _ => text.Substring(token.Start, token.Length),
            };
            // One space between tokens, none around a dot, before , ) or after ( – readable in the matrix: COUNT (A.ID), B.
            if (result.Length > 0 && previous is not ("." or "(") && value is not ("." or "," or ")"))
            {
                result.Append(' ');
            }

            result.Append(value);
            previous = value;
        }

        return result.ToString();
    }

    /// <summary>
    /// Whether <paramref name="text"/> names <paramref name="owner"/> as the schema of an object (<c>ERP.X</c>,
    /// <c>"ERP"."X"</c>): copied to another schema, the view would still read from this one.
    /// </summary>
    public static bool NamesSchema(string text, string owner)
    {
        ArgumentNullException.ThrowIfNull(text);
        var tokens = Significant(text);
        for (var i = 0; i + 1 < tokens.Count; i++)
        {
            if ((i == 0 || !tokens[i - 1].IsSymbol(".")) && IsName(tokens[i], owner) && tokens[i + 1].IsSymbol("."))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether the last tokens of <paramref name="text"/> are <paramref name="words"/> (case-insensitive).</summary>
    public static bool EndsWith(string text, params string[] words)
    {
        var tokens = Significant(text);
        return tokens.Count >= words.Length
            && words.Select((w, k) => tokens[tokens.Count - words.Length + k].IsWord(w)).All(b => b);
    }

    /// <summary>The text ends with <c>WITH CHECK OPTION</c>, perhaps followed by <c>CONSTRAINT name</c>.</summary>
    public static bool HasCheckOption(string text)
    {
        var tokens = Significant(text);
        var n = tokens.Count;
        return EndsWith(text, "WITH", "CHECK", "OPTION")
            || n >= 5 && tokens[n - 5].IsWord("WITH") && tokens[n - 4].IsWord("CHECK") && tokens[n - 3].IsWord("OPTION") && tokens[n - 2].IsWord("CONSTRAINT");
    }

    private static List<SqlToken> Significant(string text) =>
        SqlTokenizer.Tokenize(text).Where(t => t.Kind is not (SqlTokenKind.Space or SqlTokenKind.Comment)).ToList();

    /// <summary>The token is <paramref name="owner"/>: an unquoted word in any case or a quoted name written exactly so.</summary>
    private static bool IsName(SqlToken token, string owner) =>
        token.Kind == SqlTokenKind.Word && string.Equals(token.Value.ToUpperInvariant(), owner, StringComparison.Ordinal)
        || token.Kind == SqlTokenKind.QuotedIdentifier && string.Equals(token.Value, owner, StringComparison.Ordinal);

    /// <summary>A quoted name means the same without quotes only if it is upper case letters, digits, _ $ # and starts with a letter.</summary>
    private static bool NeedsQuotes(string name) =>
        name.Length == 0 || !char.IsAsciiLetterUpper(name[0]) || name.Any(c => !(char.IsAsciiLetterUpper(c) || char.IsAsciiDigit(c) || c is '_' or '$' or '#'));
}
