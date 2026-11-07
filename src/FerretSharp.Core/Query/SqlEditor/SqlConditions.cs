namespace FerretSharp.Core.Query;

/// <summary>A column a statement filters by in a WHERE (<c>"o"."STATUS" = :p0</c>), in dictionary form.</summary>
/// <param name="Qualifier">Alias or table before the column (<c>o</c>); null without one.</param>
public sealed record SqlConditionColumn(string? Qualifier, string Column);

/// <summary>
/// The columns a statement filters by (WP-76): column references in a WHERE of any level, also inside function calls
/// (<c>UPPER(k.NAME) = :p</c>), compared with a value, a bind, a list or nothing (<c>IS NULL</c>). A column compared with
/// another column (a join or a correlation, <c>c.ID = o.CUSTOMER_ID</c>) is left out, and so is the select list of a
/// subquery standing in the condition. Unqualified words may be keywords: callers keep only those naming a column they
/// know. Like <see cref="SqlAnalysis"/>, not a parser.
/// </summary>
public static class SqlConditions
{
    /// <summary>Words that end a WHERE at its level.</summary>
    private static readonly HashSet<string> WhereEnd = new(StringComparer.OrdinalIgnoreCase)
    {
        "GROUP", "ORDER", "HAVING", "CONNECT", "START", "UNION", "INTERSECT", "MINUS", "EXCEPT", "FETCH", "OFFSET", "FOR",
        "RETURNING", "MODEL", "WINDOW", "LOG",
    };

    private static readonly HashSet<string> Comparisons = new(StringComparer.OrdinalIgnoreCase)
    {
        "=", "<>", "!=", "^=", "<", ">", "<=", ">=",
    };

    /// <summary>Words that are never a column, though they stand where one could.</summary>
    private static readonly HashSet<string> Keywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "AND", "OR", "NOT", "NULL", "IS", "IN", "EXISTS", "BETWEEN", "LIKE", "ESCAPE", "CASE", "WHEN", "THEN", "ELSE", "END",
        "TRUE", "FALSE", "ANY", "ALL", "SOME", "SELECT", "FROM", "WHERE", "WITH", "AS", "PRIOR", "DISTINCT", "CAST", "DATE",
        "TIMESTAMP", "INTERVAL", "SYSDATE", "SYSTIMESTAMP", "ROWNUM", "LEVEL", "ON", "JOIN",
    };

    public static IReadOnlyList<SqlConditionColumn> Of(string statement)
    {
        var tokens = SqlTokenizer.Significant(statement);
        var result = new List<SqlConditionColumn>();

        // Per open parenthesis: whether its content is in a WHERE. A subquery starts outside, a function call or a list
        // inherits its level's state.
        var inWhere = new Stack<bool>([false]);
        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (token.IsSymbol("("))
            {
                var subquery = i + 1 < tokens.Count && (tokens[i + 1].IsWord("SELECT") || tokens[i + 1].IsWord("WITH"));
                inWhere.Push(!subquery && inWhere.Peek());
                continue;
            }

            if (token.IsSymbol(")"))
            {
                if (inWhere.Count > 1)
                {
                    inWhere.Pop();
                }

                continue;
            }

            if (token.IsWord("WHERE"))
            {
                SetTop(inWhere, true);
                continue;
            }

            if (token.Kind == SqlTokenKind.Word && WhereEnd.Contains(token.Value))
            {
                SetTop(inWhere, false);
                continue;
            }

            if (!inWhere.Peek() || ColumnAt(tokens, i) is not { } column)
            {
                continue;
            }

            // column = column: a join or a correlation, no filter – both sides are skipped.
            if (column.Next + 1 < tokens.Count && IsComparison(tokens[column.Next]) && ColumnAt(tokens, column.Next + 1) is { } other)
            {
                i = other.Next - 1;
                continue;
            }

            if (!result.Contains(column.Column))
            {
                result.Add(column.Column);
            }

            i = column.Next - 1;
        }

        return result;
    }

    private static void SetTop(Stack<bool> stack, bool value)
    {
        stack.Pop();
        stack.Push(value);
    }

    private static bool IsComparison(SqlToken token) => token.Kind == SqlTokenKind.Symbol && Comparisons.Contains(token.Value);

    /// <summary>
    /// A column reference at <paramref name="i"/>: <c>col</c>, <c>q.col</c> or <c>owner.table.col</c> (the table is the
    /// qualifier); not a function call, a keyword or a name after a dot (the reference started before).
    /// </summary>
    private static (SqlConditionColumn Column, int Next)? ColumnAt(List<SqlToken> tokens, int i)
    {
        if (!IsName(tokens, i) || (i > 0 && tokens[i - 1].IsSymbol(".")))
        {
            return null;
        }

        var parts = new List<string> { SqlAnalysis.Normalize(tokens[i]) };
        var next = i + 1;
        while (next + 1 < tokens.Count && tokens[next].IsSymbol(".") && IsName(tokens, next + 1))
        {
            parts.Add(SqlAnalysis.Normalize(tokens[next + 1]));
            next += 2;
        }

        if (next < tokens.Count && tokens[next].IsSymbol("("))
        {
            return null; // a function: UPPER(…), pkg.fn(…)
        }

        if (parts.Count == 1 && (tokens[i].Kind != SqlTokenKind.QuotedIdentifier && Keywords.Contains(parts[0])))
        {
            return null;
        }

        return parts.Count == 1
            ? (new SqlConditionColumn(null, parts[0]), next)
            : (new SqlConditionColumn(parts[^2], parts[^1]), next);
    }

    private static bool IsName(List<SqlToken> tokens, int i) =>
        i < tokens.Count && tokens[i].Kind is SqlTokenKind.Word or SqlTokenKind.QuotedIdentifier;
}
