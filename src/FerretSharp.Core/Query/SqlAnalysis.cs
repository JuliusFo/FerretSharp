using FerretSharp.Core.Resources;

namespace FerretSharp.Core.Query;

/// <summary>A table (or view, synonym) a statement names, in dictionary form (<c>kunden</c> → <c>KUNDEN</c>).</summary>
/// <param name="Depth">Parenthesis depth: 0 for the statement's own FROM/JOIN, more inside subqueries.</param>
public sealed record SqlTableReference(string? Owner, string Name, string? Alias, int Depth);

/// <summary>A bind variable compared with a column (<c>k.KUNDE_ID = :id</c>): the column suggests its type.</summary>
/// <param name="Qualifier">Alias or table before the column (<c>k</c>), in dictionary form; null without one.</param>
public sealed record SqlBindUse(string Bind, string? Qualifier, string Column);

/// <summary>What a statement is, as far as FerretSharp needs to know – not a SQL parser.</summary>
/// <param name="Binds">Bind variable names in order of first use, without duplicates (Oracle compares them case-insensitively).</param>
/// <param name="HasWhere">An UPDATE or DELETE with its own WHERE (not only one inside a subquery).</param>
/// <param name="ForUpdate">A query with FOR UPDATE (locks rows).</param>
public sealed record SqlStatementInfo(
    SqlStatementKind Kind,
    string FirstWord,
    IReadOnlyList<string> Binds,
    bool HasWhere,
    bool ForUpdate,
    IReadOnlyList<SqlTableReference> Tables,
    IReadOnlyList<SqlBindUse> BindUses)
{
    public bool IsQuery => Kind == SqlStatementKind.Query && !ForUpdate;

    public bool IsDml => Kind is SqlStatementKind.Insert or SqlStatementKind.Update or SqlStatementKind.Delete or SqlStatementKind.Merge;

    /// <summary>DDL the SQL editor runs (WP-22), a trigger too (WP-26) – it commits implicitly and cannot be undone.</summary>
    public bool IsDdl => Kind is SqlStatementKind.Ddl or SqlStatementKind.Trigger;

    /// <summary>An UPDATE or DELETE without WHERE changes every row of the table.</summary>
    public bool AffectsAllRows => Kind is SqlStatementKind.Update or SqlStatementKind.Delete && !HasWhere;

    /// <summary>Why the SQL editor does not run the statement (for the user); null if it does.</summary>
    public string? Rejection => Kind switch
    {
        SqlStatementKind.Query when ForUpdate => QueryText.SqlRejectForUpdate,
        SqlStatementKind.Query or SqlStatementKind.Insert or SqlStatementKind.Update or SqlStatementKind.Delete or SqlStatementKind.Merge => null,
        SqlStatementKind.Empty => QueryText.SqlRejectEmpty,
        SqlStatementKind.Ddl when Binds.Count > 0 => TextFormat.Format(QueryText.SqlRejectDdlBinds, Binds[0]),
        SqlStatementKind.Ddl or SqlStatementKind.Trigger => null,
        SqlStatementKind.Truncate => QueryText.SqlRejectTruncate,
        SqlStatementKind.PlSqlObject => QueryText.SqlRejectPlSqlObject,
        SqlStatementKind.SystemTrigger => QueryText.SqlRejectSystemTrigger,
        SqlStatementKind.PlSql or SqlStatementKind.Call => QueryText.SqlRejectPlSql,
        SqlStatementKind.TransactionControl => TextFormat.Format(QueryText.SqlRejectTransactionControl, FirstWord),
        SqlStatementKind.SessionControl => QueryText.SqlRejectSessionControl,
        SqlStatementKind.Lock => QueryText.SqlRejectLock,
        SqlStatementKind.Explain => QueryText.SqlRejectExplain,
        _ => TextFormat.Format(QueryText.SqlRejectOther, FirstWord),
    };
}

/// <summary>
/// What a statement does, as far as FerretSharp needs to know (part of <see cref="SqlScript"/> since R4b): its kind, binds,
/// WHERE, FOR UPDATE, the tables it names and the columns binds are compared with; the table a DDL statement changes; why
/// the schema path would refuse a statement. Deliberately not a parser.
/// </summary>
public static class SqlAnalysis
{
    /// <summary>Words that end a table reference instead of being its alias.</summary>
    private static readonly HashSet<string> NotAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        "WHERE", "ON", "JOIN", "INNER", "LEFT", "RIGHT", "FULL", "CROSS", "OUTER", "NATURAL", "GROUP", "ORDER", "HAVING", "CONNECT",
        "START", "UNION", "INTERSECT", "MINUS", "EXCEPT", "FETCH", "OFFSET", "FOR", "SET", "USING", "VALUES", "RETURNING", "RETURN",
        "WHEN", "LOG", "PARTITION", "SAMPLE", "PIVOT", "UNPIVOT", "MODEL", "WINDOW", "LATERAL", "SELECT", "WITH", "AS", "LIMIT",
        "APPLY",
    };

    /// <summary>Words that end the table list of a FROM.</summary>
    private static readonly HashSet<string> FromListEnd = new(StringComparer.OrdinalIgnoreCase)
    {
        "WHERE", "GROUP", "ORDER", "HAVING", "CONNECT", "START", "UNION", "INTERSECT", "MINUS", "EXCEPT", "FETCH", "OFFSET", "FOR",
        "MODEL", "WINDOW", "SET", "RETURNING", "LOG", "VALUES", "WHEN", "SELECT",
    };

    private static readonly HashSet<string> ComparisonOperators = new(StringComparer.OrdinalIgnoreCase)
    {
        "=", "<>", "!=", "^=", "<", ">", "<=", ">=", "LIKE",
    };

    /// <summary>
    /// Why the schema path (WP-22) would refuse <paramref name="statement"/>, for the user; null if it takes it. Lets a caller
    /// with several statements check all of them before the first runs – each DDL commits at once (R4a).
    /// </summary>
    public static string? DdlRejection(string statement)
    {
        if (SqlSplitter.Split(statement) is not [_])
        {
            return QueryText.SqlRejectNotSingle;
        }

        var info = Analyze(statement);
        return info.Rejection ?? (info.IsDdl ? null : TextFormat.Format(QueryText.SqlRejectNotDdl, info.FirstWord));
    }

    public static SqlStatementInfo Analyze(string statement)
    {
        var tokens = SqlTokenizer.Significant(statement);
        if (tokens.Count == 0)
        {
            return new SqlStatementInfo(SqlStatementKind.Empty, "", [], false, false, [], []);
        }

        var first = SqlClassifier.FirstWordOf(tokens);
        var kind = SqlClassifier.KindOf(tokens);
        var binds = new List<string>();
        var hasWhere = false;
        var forUpdate = false;
        var depth = 0;
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
            else if (token.Kind == SqlTokenKind.Bind && !binds.Contains(token.Value, StringComparer.OrdinalIgnoreCase))
            {
                binds.Add(token.Value);
            }
            else if (token.IsWord("WHERE") && depth == 0)
            {
                hasWhere = true;
            }
            else if (token.IsWord("FOR") && i + 1 < tokens.Count && tokens[i + 1].IsWord("UPDATE"))
            {
                forUpdate = true;
            }
        }

        // In a trigger ":new.x", ":old.x" (and the names of its REFERENCING clause) are its rows, not values to ask for.
        return kind is SqlStatementKind.Trigger or SqlStatementKind.SystemTrigger
            ? new SqlStatementInfo(kind, first, [], hasWhere, forUpdate, TablesOf(tokens), [])
            : new SqlStatementInfo(kind, first, binds, hasWhere, forUpdate, TablesOf(tokens), BindUsesOf(tokens));
    }

    /// <summary>
    /// The table a DDL statement changes (WP-22): <c>ALTER TABLE x</c>, <c>DROP TABLE x</c>, <c>TRUNCATE TABLE x</c>,
    /// <c>FLASHBACK TABLE x</c>, <c>RENAME x TO …</c>, <c>CREATE [UNIQUE | BITMAP] INDEX i ON x</c>, <c>COMMENT ON TABLE x</c>,
    /// <c>COMMENT ON COLUMN [owner.]x.c</c> – in dictionary form; null for anything else (a new table, a view, GRANT …).
    /// </summary>
    public static SqlTableReference? DdlTableOf(string statement)
    {
        var tokens = SqlTokenizer.Significant(statement);
        if (tokens.Count < 2 || SqlClassifier.KindOf(tokens) is not (SqlStatementKind.Ddl or SqlStatementKind.Truncate))
        {
            return null;
        }

        var first = SqlClassifier.FirstWordOf(tokens);
        if (first == "COMMENT" && tokens.Count > 3 && tokens[1].IsWord("ON") && tokens[2].IsWord("COLUMN"))
        {
            return ColumnTable(tokens);
        }

        int? at = first switch
        {
            "ALTER" or "DROP" or "TRUNCATE" or "FLASHBACK" when tokens[1].IsWord("TABLE") => 2,
            "RENAME" => 1,
            "CREATE" => IndexOn(tokens),
            "COMMENT" when tokens.Count > 3 && tokens[1].IsWord("ON") && tokens[2].IsWord("TABLE") => 3,
            _ => null,
        };

        return at is { } i ? DdlTargetAt(tokens, i) : null;
    }

    /// <summary>
    /// The object a DDL statement names at <paramref name="i"/>: <c>[IF [NOT] EXISTS] [owner.]name</c>. Unlike a table in a
    /// query, a DDL target has no alias – every word is a name here (R4a: <c>ALTER TABLE log …</c> was not recognised, the
    /// table not taken first, and the ALTER could wait without a way to cancel it).
    /// </summary>
    private static SqlTableReference? DdlTargetAt(List<SqlToken> tokens, int i)
    {
        if (i + 1 < tokens.Count && tokens[i].IsWord("IF"))
        {
            i += tokens[i + 1].IsWord("EXISTS") ? 2 : tokens[i + 1].IsWord("NOT") && i + 2 < tokens.Count && tokens[i + 2].IsWord("EXISTS") ? 3 : 0;
        }

        if (DdlName(tokens, i) is not { } first)
        {
            return null;
        }

        return i + 2 < tokens.Count && tokens[i + 1].IsSymbol(".") && DdlName(tokens, i + 2) is { } second
            ? new SqlTableReference(first, second, null, 0)
            : new SqlTableReference(null, first, null, 0);
    }

    /// <summary>A name in dictionary form, keywords included (see <see cref="DdlTargetAt"/>); null for anything else.</summary>
    private static string? DdlName(List<SqlToken> tokens, int i) =>
        i < tokens.Count && tokens[i].Kind is SqlTokenKind.Word or SqlTokenKind.QuotedIdentifier ? Normalize(tokens[i]) : null;

    /// <summary><c>CREATE [UNIQUE | BITMAP | MULTIVALUE] INDEX [IF NOT EXISTS] name ON x</c>: the position of x.</summary>
    private static int? IndexOn(List<SqlToken> tokens)
    {
        var index = tokens.FindIndex(t => t.IsWord("INDEX"));
        if (index is < 1 or > 2 || tokens.Take(index).Skip(1).Any(t => !(t.IsWord("UNIQUE") || t.IsWord("BITMAP") || t.IsWord("MULTIVALUE"))))
        {
            return null;
        }

        var on = tokens.FindIndex(index, t => t.IsWord("ON"));
        return on > 0 ? on + 1 : null;
    }

    /// <summary><c>COMMENT ON COLUMN owner.table.column</c> or <c>table.column</c>: the table, with its owner if given.</summary>
    private static SqlTableReference? ColumnTable(List<SqlToken> tokens)
    {
        // owner . table . column IS '…' – names separated by dots up to IS
        var parts = new List<string>();
        for (var i = 3; i < tokens.Count; i += 2)
        {
            if (DdlName(tokens, i) is not { } name)
            {
                return null;
            }

            parts.Add(name);
            if (i + 1 >= tokens.Count || tokens[i + 1].IsWord("IS"))
            {
                break;
            }

            if (!tokens[i + 1].IsSymbol("."))
            {
                return null;
            }
        }

        return parts switch
        {
            [var owner, var table, _] => new SqlTableReference(owner, table, null, 0),
            [var table, _] => new SqlTableReference(null, table, null, 0),
            _ => null,
        };
    }

    /// <summary>
    /// Tables after FROM (and the commas of its list), JOIN, INTO, USING and a leading UPDATE, with their aliases. Subqueries
    /// in FROM are skipped as tables; their own FROM counts at a deeper level.
    /// </summary>
    private static List<SqlTableReference> TablesOf(List<SqlToken> tokens)
    {
        var tables = new List<SqlTableReference>();
        var depth = 0;
        var fromListDepth = new HashSet<int>();
        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (token.IsSymbol("("))
            {
                depth++;
                continue;
            }

            if (token.IsSymbol(")"))
            {
                fromListDepth.Remove(depth);
                depth = Math.Max(0, depth - 1);
                continue;
            }

            if (token.Kind == SqlTokenKind.Word && FromListEnd.Contains(token.Value))
            {
                fromListDepth.Remove(depth);
            }

            var startsReference = token.IsWord("FROM") || token.IsWord("JOIN") || token.IsWord("INTO") || token.IsWord("USING")
                                  || (i == 0 && token.IsWord("UPDATE"))
                                  || (token.IsSymbol(",") && fromListDepth.Contains(depth));
            if (!startsReference)
            {
                continue;
            }

            if (token.IsWord("FROM"))
            {
                fromListDepth.Add(depth);
            }

            if (ReferenceAt(tokens, i + 1, depth) is { } reference)
            {
                tables.Add(reference.Table);
                i = reference.Next - 1;
            }
        }

        return tables;
    }

    private static (SqlTableReference Table, int Next)? ReferenceAt(List<SqlToken> tokens, int i, int depth)
    {
        if (Name(tokens, i) is not { } first)
        {
            return null; // a subquery, or no name at all
        }

        string? owner = null;
        var name = first;
        var next = i + 1;
        if (next + 1 < tokens.Count && tokens[next].IsSymbol(".") && Name(tokens, next + 1) is { } second)
        {
            owner = first;
            name = second;
            next += 2;
        }

        if (next < tokens.Count && tokens[next].IsSymbol("@"))
        {
            next += 2; // a database link: @name
        }

        if (next < tokens.Count && tokens[next].IsWord("AS") && !(next + 1 < tokens.Count && tokens[next + 1].IsWord("OF")))
        {
            next++;
        }

        string? alias = null;
        if (next < tokens.Count && (tokens[next].Kind == SqlTokenKind.QuotedIdentifier
                                    || (tokens[next].Kind == SqlTokenKind.Word && !NotAliases.Contains(tokens[next].Value))))
        {
            alias = Normalize(tokens[next]);
            next++;
        }

        return (new SqlTableReference(owner, name, alias, depth), next);
    }

    /// <summary>Binds compared with a column: <c>col = :b</c>, <c>:b = t.col</c>, <c>col BETWEEN :a AND :b</c>, <c>col IN (:a, :b)</c>.</summary>
    private static List<SqlBindUse> BindUsesOf(List<SqlToken> tokens)
    {
        var uses = new List<SqlBindUse>();
        for (var k = 0; k < tokens.Count; k++)
        {
            if (tokens[k].Kind != SqlTokenKind.Bind)
            {
                continue;
            }

            var bind = tokens[k].Value;
            (string? Qualifier, string Column)? column = null;
            if (k >= 2 && IsComparison(tokens[k - 1]))
            {
                column = ColumnBefore(tokens, k - 2);
            }
            else if (k + 2 < tokens.Count && IsComparison(tokens[k + 1]))
            {
                column = ColumnAfter(tokens, k + 2);
            }
            else if (k >= 2 && tokens[k - 1].IsWord("BETWEEN"))
            {
                column = ColumnBefore(tokens, k - 2);
            }
            else if (k >= 4 && tokens[k - 1].IsWord("AND") && tokens[k - 3].IsWord("BETWEEN"))
            {
                column = ColumnBefore(tokens, k - 4);
            }
            else if (InListColumn(tokens, k) is { } inList)
            {
                column = inList;
            }

            if (column is { } c)
            {
                uses.Add(new SqlBindUse(bind, c.Qualifier, c.Column));
            }
        }

        return uses;
    }

    private static (string? Qualifier, string Column)? InListColumn(List<SqlToken> tokens, int k)
    {
        var i = k - 1;
        while (i >= 0 && (tokens[i].IsSymbol(",") || tokens[i].Kind is SqlTokenKind.Bind or SqlTokenKind.Text or SqlTokenKind.Number))
        {
            i--;
        }

        return i >= 2 && tokens[i].IsSymbol("(") && tokens[i - 1].IsWord("IN") ? ColumnBefore(tokens, i - 2) : null;
    }

    private static (string? Qualifier, string Column)? ColumnBefore(List<SqlToken> tokens, int i)
    {
        if (i < 0 || Name(tokens, i) is not { } column)
        {
            return null;
        }

        return i >= 2 && tokens[i - 1].IsSymbol(".") && Name(tokens, i - 2) is { } qualifier ? (qualifier, column) : (null, column);
    }

    private static (string? Qualifier, string Column)? ColumnAfter(List<SqlToken> tokens, int i)
    {
        if (Name(tokens, i) is not { } first)
        {
            return null;
        }

        return i + 2 < tokens.Count && tokens[i + 1].IsSymbol(".") && Name(tokens, i + 2) is { } column ? (first, column) : (null, first);
    }

    private static bool IsComparison(SqlToken token) =>
        token.Kind is SqlTokenKind.Symbol or SqlTokenKind.Word && ComparisonOperators.Contains(token.Value);

    /// <summary>A name token in dictionary form; null for anything else (keywords that end a reference included).</summary>
    private static string? Name(List<SqlToken> tokens, int i) =>
        i >= 0 && i < tokens.Count && tokens[i].Kind switch
        {
            SqlTokenKind.QuotedIdentifier => true,
            SqlTokenKind.Word => !NotAliases.Contains(tokens[i].Value),
            _ => false,
        }
            ? Normalize(tokens[i])
            : null;

    private static string Normalize(SqlToken token) =>
        token.Kind == SqlTokenKind.QuotedIdentifier ? token.Value : token.Value.ToUpperInvariant();
}
