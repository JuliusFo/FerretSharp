using FerretSharp.Core.Data;
using FerretSharp.Core.Schema;

namespace FerretSharp.Core.Query;

/// <summary>A result column traced back to a table column.</summary>
/// <param name="Source">
/// The row source the column belongs to – the alias or table it was read through. Columns with the same source come from the
/// same row of <paramref name="Table"/>; two aliases of one table are two sources.
/// </param>
/// <param name="Column">The column's dictionary name; <see cref="SqlResultSources"/> checks that the table has it.</param>
public sealed record SqlResultOrigin(string Source, TableSummary Table, string Column);

/// <summary>
/// Where the columns of a free query come from (WP-48): the driver's origin (<see cref="SqlColumnOrigin"/>) checked against
/// the statement and the schema. The driver names the table as written, or the alias for a quoted one (EF Core's
/// <c>"a"</c>) – the statement's FROM and JOIN say which table an alias stands for (<see cref="SqlAnalysis"/>). Anything
/// unclear has no origin: a column with a wrong one would jump to unrelated rows. Not a parser.
/// </summary>
public static class SqlResultOrigins
{
    private static readonly HashSet<string> SetOperators = new(StringComparer.OrdinalIgnoreCase) { "UNION", "INTERSECT", "MINUS", "EXCEPT" };

    /// <summary>
    /// The origin of each result column in <paramref name="columns"/> order; null where there is none or it is unclear:
    /// <list type="bullet">
    /// <item>every column of a statement with <c>WITH</c> or a set operator (the driver reports the first branch for all rows),</item>
    /// <item>a column of an inline view or <c>CROSS APPLY</c> (reported as its alias),</item>
    /// <item>a table the schema does not know, or one the statement names twice in its own FROM without telling the driver
    /// which (a self-join with unquoted aliases), or an alias that stands for different tables.</item>
    /// </list>
    /// </summary>
    public static IReadOnlyList<SqlResultOrigin?> Resolve(string sql, IReadOnlyList<SqlColumn> columns, SchemaCache schema)
    {
        var none = new SqlResultOrigin?[columns.Count];
        if (columns.All(c => c.Origin?.Table is null))
        {
            return none;
        }

        var tokens = SqlTokenizer.Significant(sql);
        if (tokens.Count == 0 || tokens[0].IsWord("WITH") || HasSetOperator(tokens))
        {
            return none;
        }

        var statement = new Statement(SqlAnalysis.Analyze(sql).Tables, SubqueryAliases(tokens), schema);
        return columns.Select(c => c.Origin is { Table: { } table } origin && statement.SourceOf(origin.Owner, table) is { } source
            ? new SqlResultOrigin(source.Key, source.Table, origin.Column)
            : null).ToList();
    }

    /// <summary>A set operator outside of parentheses: the statement's rows come from several queries.</summary>
    private static bool HasSetOperator(List<SqlToken> tokens)
    {
        var depth = 0;
        foreach (var token in tokens)
        {
            if (token.IsSymbol("("))
            {
                depth++;
            }
            else if (token.IsSymbol(")"))
            {
                depth = Math.Max(0, depth - 1);
            }
            else if (depth == 0 && token.Kind == SqlTokenKind.Word && SetOperators.Contains(token.Value))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Names after a closing parenthesis of a subquery – <c>(SELECT …) x</c>, <c>(SELECT …) AS "t"</c> – in dictionary
    /// form: aliases of inline views (and of scalar subqueries in the select list, which never name a table).
    /// </summary>
    private static HashSet<string> SubqueryAliases(List<SqlToken> tokens)
    {
        var aliases = new HashSet<string>(StringComparer.Ordinal);
        var opened = new Stack<bool>(); // per open parenthesis: does a query start in it?
        for (var i = 0; i < tokens.Count; i++)
        {
            if (tokens[i].IsSymbol("("))
            {
                opened.Push(i + 1 < tokens.Count && (tokens[i + 1].IsWord("SELECT") || tokens[i + 1].IsWord("WITH")));
                continue;
            }

            if (!tokens[i].IsSymbol(")") || !opened.TryPop(out var query) || !query)
            {
                continue;
            }

            var next = i + 1 < tokens.Count && tokens[i + 1].IsWord("AS") ? i + 2 : i + 1;
            if (next < tokens.Count && (tokens[next].Kind == SqlTokenKind.QuotedIdentifier
                                        || (tokens[next].Kind == SqlTokenKind.Word && !SqlAnalysis.NotAliases.Contains(tokens[next].Value))))
            {
                aliases.Add(Normalize(tokens[next]));
            }
        }

        return aliases;
    }

    private static string Normalize(SqlToken token) =>
        token.Kind == SqlTokenKind.QuotedIdentifier ? token.Value : token.Value.ToUpperInvariant();

    private sealed record Source(string Key, TableSummary Table);

    private sealed class Statement(IReadOnlyList<SqlTableReference> references, HashSet<string> subqueryAliases, SchemaCache schema)
    {
        private readonly Dictionary<(string?, string), Source?> _sources = [];

        /// <summary>The row source behind a name the driver reports; null if unclear (cached: many columns share one).</summary>
        public Source? SourceOf(string? owner, string name)
        {
            if (!_sources.TryGetValue((owner, name), out var source))
            {
                source = _sources[(owner, name)] = Find(owner, name);
            }

            return source;
        }

        private Source? Find(string? owner, string name)
        {
            if (owner is not null)
            {
                // The driver only names the owner if the statement does.
                var qualified = references.Where(r => r.Owner == owner && r.Name == name).ToList();
                return qualified.Count(r => r.Depth == 0) <= 1 && OneTable(qualified) is { } table ? new Source($"{owner}.{name}", table) : null;
            }

            if (subqueryAliases.Contains(name))
            {
                return null;
            }

            var aliased = references.Where(r => r.Alias == name).ToList();
            var named = references.Where(r => r.Owner is null && r.Name == name && r.Alias != name).ToList();
            if (aliased.Count > 0)
            {
                // A name that is an alias here and a table there (FROM a KUNDEN JOIN KUNDEN k): the driver could mean either.
                return named.Count == 0 && OneTable(aliased) is { } table ? new Source($"\"{name}\"", table) : null;
            }

            // The driver resolves an unquoted alias to the table: two of them in the same FROM cannot be told apart.
            return named.Count(r => r.Depth == 0) <= 1 && OneTable(named) is { } named1 ? new Source(name, named1) : null;
        }

        /// <summary>The table all references stand for; null if none, unknown or different ones.</summary>
        private TableSummary? OneTable(List<SqlTableReference> candidates)
        {
            var tables = candidates.Select(r => SqlCompletion.Resolve(schema, r)).ToList();
            return tables is [{ } first, ..] && tables.All(t => t is not null && t.Ref == first.Ref) ? first : null;
        }
    }
}
