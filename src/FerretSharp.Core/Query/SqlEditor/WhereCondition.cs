using FerretSharp.Core.Oracle;
using FerretSharp.Core.Resources;
using FerretSharp.Core.Schema;

namespace FerretSharp.Core.Query;

/// <summary>
/// A condition the user writes for a table tab instead of filter rows (WP-79, <see cref="RowFilter.Sql"/>): checked before it is
/// put into a statement, so it stays a condition – for correctness, not for safety. The session's read-only tripwire still checks
/// the whole statement as it does for every query. Built on the SQL editor's tokenizer, not a parser: what is not caught here
/// comes back as Oracle's error.
/// </summary>
public static class WhereCondition
{
    /// <summary>
    /// Reserved words that end a WHERE at its level (<c>ORDER BY</c>, <c>UNION</c>, <c>FOR UPDATE</c> …): at the top level they
    /// would change the statement around the condition. Only reserved words – a column may be called <c>OFFSET</c> or <c>MODEL</c>.
    /// </summary>
    private static readonly HashSet<string> Ends = new(StringComparer.OrdinalIgnoreCase)
    {
        "ORDER", "GROUP", "HAVING", "CONNECT", "START", "UNION", "INTERSECT", "MINUS", "FOR",
    };

    /// <summary>The condition in a statement: in its own parentheses and lines, so a <c>--</c> comment at its end stays in it.</summary>
    public static string Wrap(string condition) => "(\n" + condition + "\n)";

    /// <summary>
    /// The condition as the WHERE of a query on <paramref name="table"/>, for the SQL editor's completion (<see cref="SqlCompletion"/>):
    /// the columns of the table – also by its alias <c>t</c>, as in the grid's statement – and the tables of subqueries.
    /// </summary>
    /// <param name="cursor">The cursor in <paramref name="condition"/>.</param>
    /// <returns>The statement and the cursor in it.</returns>
    public static (string Statement, int Cursor) ForCompletion(TableRef table, string condition, int cursor)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(condition);
        var prefix = $"SELECT *\n  FROM {OracleIdentifier.Qualify(table.Owner, table.Name)} t\n WHERE ";
        return (prefix + condition, prefix.Length + Math.Clamp(cursor, 0, condition.Length));
    }

    /// <returns>Why <paramref name="condition"/> cannot stand after <c>WHERE</c>; null if it can.</returns>
    public static string? Problem(string condition)
    {
        ArgumentNullException.ThrowIfNull(condition);

        // A string, quoted name or comment left open swallows the line end after it.
        var tokens = SqlTokenizer.Tokenize(condition + "\n");
        if (tokens[^1].Kind != SqlTokenKind.Space)
        {
            return tokens[^1].Kind switch
            {
                SqlTokenKind.Comment => QueryText.ConditionCommentOpen,
                SqlTokenKind.QuotedIdentifier => QueryText.ConditionNameOpen,
                _ => QueryText.ConditionTextOpen,
            };
        }

        var significant = tokens.Where(t => t.Kind is not (SqlTokenKind.Space or SqlTokenKind.Comment)).ToList();
        if (significant.Count == 0)
        {
            return QueryText.ConditionEmpty;
        }

        var depth = 0;
        for (var i = 0; i < significant.Count; i++)
        {
            var token = significant[i];
            if (token.Kind == SqlTokenKind.Bind)
            {
                return TextFormat.Format(QueryText.ConditionBind, ":" + token.Value);
            }

            if (token.IsSymbol("("))
            {
                depth++;
            }
            else if (token.IsSymbol(")") && --depth < 0)
            {
                return QueryText.ConditionParenthesisClosed;
            }
            else if (depth == 0 && TopLevelEnd(significant, i) is { } end)
            {
                return TextFormat.Format(QueryText.ConditionTopLevel, end);
            }
        }

        return depth > 0 ? QueryText.ConditionParenthesisOpen : null;
    }

    /// <summary>What ends the WHERE at the top level from token <paramref name="i"/> on, as written; null if nothing does.</summary>
    private static string? TopLevelEnd(List<SqlToken> tokens, int i)
    {
        var token = tokens[i];
        if (token.IsSymbol(";"))
        {
            return ";";
        }

        if (token.Kind != SqlTokenKind.Word)
        {
            return null;
        }

        if (Ends.Contains(token.Value))
        {
            return token.Value.ToUpperInvariant();
        }

        // FETCH FIRST/NEXT … and OFFSET n ROW(S): not reserved, a column may have these names.
        var next = i + 1 < tokens.Count ? tokens[i + 1] : default;
        if (token.IsWord("FETCH") && (next.IsWord("FIRST") || next.IsWord("NEXT")))
        {
            return "FETCH";
        }

        if (token.IsWord("OFFSET") && i + 2 < tokens.Count && (tokens[i + 2].IsWord("ROW") || tokens[i + 2].IsWord("ROWS")))
        {
            return "OFFSET";
        }

        return null;
    }
}
