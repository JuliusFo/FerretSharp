using FerretSharp.Core.Data;

namespace FerretSharp.Core.Query;

/// <summary>A result column to sort by – its position in the result (0-based) – as a header click asks for it.</summary>
public sealed record ResultSort(int Column, bool Descending);

/// <summary>
/// A query FerretSharp did not build (SQL editor, LINQ console), sorted by its result columns: ORDER BY by position, so
/// expressions and columns of the same name sort too. Oracle's own NULL order (last ascending, first descending) stays.
/// <list type="bullet">
/// <item>Without row limiting on its top level the order goes into the query itself: in front of its own ORDER BY, which
/// then breaks ties, or appended. <c>WHERE ROWNUM &lt;= n</c> limits before ORDER BY, so that sorts the same rows.</item>
/// <item>With <c>FETCH</c>/<c>OFFSET</c> (or <c>ORDER SIBLINGS BY</c>) on its top level the query is wrapped
/// (<c>SELECT * FROM (…) ORDER BY 2</c>) – the rows it limits to, re-sorted. Only with distinct column names:
/// <c>SELECT *</c> over a view with two columns of the same name fails with ORA-00918 (joins of EF Core).</item>
/// </list>
/// Binds stay as they are: the text only grows around the query.
/// </summary>
public static class SqlResultOrder
{
    /// <summary>The query sorted by <paramref name="sorts"/>; itself without sorts; null if it cannot be sorted by the database.</summary>
    /// <param name="columns">The result's columns as the first page reported them.</param>
    public static QuerySpec? Sorted(QuerySpec query, IReadOnlyList<SqlColumn> columns, IReadOnlyList<ResultSort> sorts)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(sorts);
        if (sorts.Count == 0)
        {
            return query;
        }

        var keys = string.Join(", ", sorts.Select(s => (s.Column + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) + (s.Descending ? " DESC" : "")));
        var sql = query.Sql;
        return TopLevelOf(sql) switch
        {
            { Limited: false, OrderBy: { } orderBy } => query with { Sql = $"{sql[..orderBy]} {keys},{sql[orderBy..]}" },
            { Limited: false } => query with { Sql = $"{sql}\nORDER BY {keys}" }, // after a line comment at the end too
            _ when columns.Select(c => c.Name).Distinct(StringComparer.Ordinal).Count() == columns.Count =>
                query with { Sql = $"SELECT * FROM (\n{sql}\n) ORDER BY {keys}" },
            _ => null,
        };
    }

    /// <summary>Whether the database can sort the query by its columns at all (<see cref="Sorted"/> gives a query).</summary>
    public static bool CanSort(QuerySpec query, IReadOnlyList<SqlColumn> columns) =>
        Sorted(query, columns, [new ResultSort(0, false)]) is not null;

    /// <param name="Limited">FETCH, OFFSET or ORDER SIBLINGS BY on the top level: an order of its own would change the rows.</param>
    /// <param name="OrderBy">Where the keys of the top level's ORDER BY start (after <c>BY</c>); null without one.</param>
    private readonly record struct TopLevel(bool Limited, int? OrderBy);

    private static TopLevel TopLevelOf(string sql)
    {
        var tokens = SqlTokenizer.Significant(sql);
        var depth = 0;
        int? orderBy = null;
        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (token.IsSymbol("("))
            {
                depth++;
            }
            else if (token.IsSymbol(")"))
            {
                depth = Math.Max(0, depth - 1);
            }
            else if (depth > 0)
            {
                continue;
            }
            else if (token.IsWord("FETCH") || token.IsWord("OFFSET"))
            {
                return new TopLevel(true, orderBy);
            }
            else if (token.IsWord("ORDER") && i + 1 < tokens.Count)
            {
                if (tokens[i + 1].IsWord("SIBLINGS"))
                {
                    return new TopLevel(true, null);
                }

                if (tokens[i + 1].IsWord("BY"))
                {
                    orderBy = tokens[i + 1].End;
                }
            }
        }

        return new TopLevel(false, orderBy);
    }
}
