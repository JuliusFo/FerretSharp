namespace FerretSharp.Core.Query;

public enum SqlTokenKind
{
    Word,

    /// <summary><c>"Name"</c>; <see cref="SqlToken.Value"/> is the name without quotes.</summary>
    QuotedIdentifier,

    /// <summary>A string literal, also <c>N'…'</c> and <c>q'[…]'</c>.</summary>
    Text,
    Number,

    /// <summary><c>:name</c> or <c>:1</c>; <see cref="SqlToken.Value"/> is the name without the colon.</summary>
    Bind,

    /// <summary>One character, or one of <c>&lt;= &gt;= &lt;&gt; != ^= || := =&gt;</c>.</summary>
    Symbol,
    Comment,
    Space,
}

/// <param name="Value">The token as written; for quoted identifiers and binds without quotes or colon.</param>
public readonly record struct SqlToken(SqlTokenKind Kind, int Start, int Length, string Value)
{
    public int End => Start + Length;

    public bool IsWord(string word) => Kind == SqlTokenKind.Word && string.Equals(Value, word, StringComparison.OrdinalIgnoreCase);

    public bool IsSymbol(string symbol) => Kind == SqlTokenKind.Symbol && Value == symbol;
}

/// <summary>
/// The SQL editor's tokens (WP-17, part of <see cref="SqlScript"/> since R4b): comments, string literals including
/// <c>q'[…]'</c>, quoted names, bind variables, numbers, words and symbols. The session's statement guards are built on it,
/// so they see a script exactly as the editor does.
/// </summary>
public static class SqlTokenizer
{
    private static readonly string[] TwoCharSymbols = ["<=", ">=", "<>", "!=", "^=", "||", ":=", "=>"];

    public static IReadOnlyList<SqlToken> Tokenize(string text)
    {
        var tokens = new List<SqlToken>();
        var i = 0;
        while (i < text.Length)
        {
            var start = i;
            var c = text[i];
            SqlTokenKind kind;
            string? value = null;
            if (char.IsWhiteSpace(c))
            {
                while (i < text.Length && char.IsWhiteSpace(text[i]))
                {
                    i++;
                }

                kind = SqlTokenKind.Space;
            }
            else if (c == '-' && At(text, i + 1) == '-')
            {
                while (i < text.Length && text[i] != '\n')
                {
                    i++;
                }

                kind = SqlTokenKind.Comment;
            }
            else if (c == '/' && At(text, i + 1) == '*')
            {
                var end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? text.Length : end + 2;
                kind = SqlTokenKind.Comment;
            }
            else if (QuoteStart(text, i) is { } quote)
            {
                i = StringEnd(text, quote);
                kind = SqlTokenKind.Text;
            }
            else if (c == '"')
            {
                var end = text.IndexOf('"', i + 1);
                i = end < 0 ? text.Length : end + 1;
                kind = SqlTokenKind.QuotedIdentifier;
                value = text[(start + 1)..(end < 0 ? text.Length : end)];
            }
            else if (c == ':' && At(text, i + 1) is { } next && (IsWordChar(next) || next == '"'))
            {
                if (next == '"')
                {
                    var end = text.IndexOf('"', i + 2);
                    i = end < 0 ? text.Length : end + 1;
                    value = text[(start + 2)..(end < 0 ? text.Length : end)];
                }
                else
                {
                    i++;
                    while (i < text.Length && IsWordChar(text[i]))
                    {
                        i++;
                    }

                    value = text[(start + 1)..i];
                }

                kind = SqlTokenKind.Bind;
            }
            else if (char.IsAsciiDigit(c) || (c == '.' && At(text, i + 1) is { } digit && char.IsAsciiDigit(digit)))
            {
                while (i < text.Length && (char.IsAsciiDigit(text[i]) || text[i] == '.'))
                {
                    i++;
                }

                if (i < text.Length && text[i] is 'e' or 'E' && At(text, i + 1) is { } exponent
                    && (char.IsAsciiDigit(exponent) || (exponent is '+' or '-' && At(text, i + 2) is { } d && char.IsAsciiDigit(d))))
                {
                    i += 2;
                    while (i < text.Length && char.IsAsciiDigit(text[i]))
                    {
                        i++;
                    }
                }

                kind = SqlTokenKind.Number;
            }
            else if (IsWordStart(c))
            {
                while (i < text.Length && IsWordChar(text[i]))
                {
                    i++;
                }

                kind = SqlTokenKind.Word;
            }
            else
            {
                i += i + 1 < text.Length && TwoCharSymbols.Contains(text.Substring(i, 2)) ? 2 : 1;
                kind = SqlTokenKind.Symbol;
            }

            tokens.Add(new SqlToken(kind, start, i - start, value ?? text[start..i]));
        }

        return tokens;
    }

    /// <summary>The tokens without spaces and comments: what statements are told apart by.</summary>
    internal static List<SqlToken> Significant(string text) =>
        Tokenize(text).Where(t => t.Kind is not (SqlTokenKind.Space or SqlTokenKind.Comment)).ToList();

    private static char? At(string text, int i) => i < text.Length ? text[i] : null;

    private static bool IsWordStart(char c) => char.IsLetter(c) || c == '_';

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '$' or '#';

    /// <summary>Where a string literal starts: <c>'</c>, <c>N'</c>, <c>q'X</c>, <c>nq'X</c>; null if none starts here.</summary>
    private static (int Content, char Close, bool Alternative)? QuoteStart(string text, int i)
    {
        if (text[i] != '\'' && i > 0 && IsWordChar(text[i - 1]))
        {
            return null; // a prefix letter must start a word: "abn'x'" has no N'…' literal
        }

        var j = i;
        if (text[j] is 'n' or 'N')
        {
            j++;
        }

        var alternative = At(text, j) is 'q' or 'Q' && At(text, j + 1) == '\'';
        if (alternative)
        {
            j++;
        }

        if (At(text, j) != '\'')
        {
            return null;
        }

        if (!alternative)
        {
            return (j + 1, '\'', false);
        }

        if (At(text, j + 1) is not { } open || char.IsWhiteSpace(open))
        {
            return null;
        }

        var close = open switch { '[' => ']', '{' => '}', '(' => ')', '<' => '>', _ => open };
        return (j + 2, close, true);
    }

    private static int StringEnd(string text, (int Content, char Close, bool Alternative) quote)
    {
        if (quote.Alternative)
        {
            var end = text.IndexOf(quote.Close + "'", quote.Content, StringComparison.Ordinal);
            return end < 0 ? text.Length : end + 2;
        }

        var i = quote.Content;
        while (i < text.Length)
        {
            if (text[i] == '\'')
            {
                if (At(text, i + 1) == '\'')
                {
                    i += 2;
                    continue;
                }

                return i + 1;
            }

            i++;
        }

        return text.Length;
    }
}
