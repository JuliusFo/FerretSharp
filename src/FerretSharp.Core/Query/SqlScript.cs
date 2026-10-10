using System.Text.RegularExpressions;
using FerretSharp.Core.Resources;

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

/// <summary>A statement of a script: where it is and its text without the terminating <c>;</c>.</summary>
public sealed record SqlStatement(int Start, int Length, string Text)
{
    public int End => Start + Length;
}

public enum SqlStatementKind
{
    Empty,
    Query,
    Insert,
    Update,
    Delete,
    Merge,

    /// <summary>CREATE, ALTER (except SESSION/SYSTEM), DROP, GRANT … – commits implicitly (WP-22: runs without an open transaction).</summary>
    Ddl,

    /// <summary>TRUNCATE: DDL that deletes every row without a way back – never run (decision of the user, WP-22).</summary>
    Truncate,

    /// <summary>CREATE [OR REPLACE] PROCEDURE, FUNCTION, PACKAGE, TRIGGER, TYPE, JAVA: DDL with a PL/SQL body.</summary>
    PlSqlObject,
    PlSql,
    Call,

    /// <summary>COMMIT, ROLLBACK, SAVEPOINT, SET TRANSACTION.</summary>
    TransactionControl,

    /// <summary>ALTER SESSION, ALTER SYSTEM.</summary>
    SessionControl,

    /// <summary>LOCK TABLE.</summary>
    Lock,
    Explain,
    Unknown,
}

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

    /// <summary>DDL the SQL editor runs (WP-22) – it commits implicitly and cannot be undone.</summary>
    public bool IsDdl => Kind == SqlStatementKind.Ddl;

    /// <summary>An UPDATE or DELETE without WHERE changes every row of the table.</summary>
    public bool AffectsAllRows => Kind is SqlStatementKind.Update or SqlStatementKind.Delete && !HasWhere;

    /// <summary>Why the SQL editor does not run the statement (for the user); null if it does.</summary>
    public string? Rejection => Kind switch
    {
        SqlStatementKind.Query when ForUpdate => QueryText.SqlRejectForUpdate,
        SqlStatementKind.Query or SqlStatementKind.Insert or SqlStatementKind.Update or SqlStatementKind.Delete or SqlStatementKind.Merge => null,
        SqlStatementKind.Empty => QueryText.SqlRejectEmpty,
        SqlStatementKind.Ddl when Binds.Count > 0 => TextFormat.Format(QueryText.SqlRejectDdlBinds, Binds[0]),
        SqlStatementKind.Ddl => null,
        SqlStatementKind.Truncate => QueryText.SqlRejectTruncate,
        SqlStatementKind.PlSqlObject => QueryText.SqlRejectPlSqlObject,
        SqlStatementKind.PlSql or SqlStatementKind.Call => QueryText.SqlRejectPlSql,
        SqlStatementKind.TransactionControl => TextFormat.Format(QueryText.SqlRejectTransactionControl, FirstWord),
        SqlStatementKind.SessionControl => QueryText.SqlRejectSessionControl,
        SqlStatementKind.Lock => QueryText.SqlRejectLock,
        SqlStatementKind.Explain => QueryText.SqlRejectExplain,
        _ => TextFormat.Format(QueryText.SqlRejectOther, FirstWord),
    };
}

/// <summary>
/// The free SQL editor's view of a script (WP-17): tokens (comments, literals including <c>q'[…]'</c>, quoted names, binds),
/// statements separated by <c>;</c>, a blank line or a line with only <c>/</c>, the statement at the cursor, and what kind
/// of statement it is. Deliberately not a parser – the database decides what is valid, the session guards decide what runs.
/// </summary>
public static class SqlScript
{
    private static readonly Regex BlankLine = new(@"\n[ \t\r\f\v]*\n", RegexOptions.CultureInvariant);

    private static readonly string[] TwoCharSymbols = ["<=", ">=", "<>", "!=", "^=", "||", ":=", "=>"];

    private static readonly HashSet<string> DdlWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "CREATE", "ALTER", "DROP", "RENAME", "COMMENT", "GRANT", "REVOKE", "ANALYZE", "AUDIT", "NOAUDIT", "FLASHBACK", "PURGE",
        "ASSOCIATE", "DISASSOCIATE",
    };

    /// <summary>Objects whose CREATE carries a PL/SQL (or Java) body.</summary>
    private static readonly HashSet<string> PlSqlObjects = new(StringComparer.OrdinalIgnoreCase)
    {
        "PROCEDURE", "FUNCTION", "PACKAGE", "TRIGGER", "TYPE", "JAVA",
    };

    /// <summary>Words between CREATE and the object type: <c>OR REPLACE</c>, <c>EDITIONABLE</c>, <c>AND COMPILE</c> (Java) ….</summary>
    private static readonly HashSet<string> CreateModifiers = new(StringComparer.OrdinalIgnoreCase)
    {
        "OR", "REPLACE", "EDITIONABLE", "NONEDITIONABLE", "AND", "COMPILE", "RESOLVE", "NOFORCE",
    };

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

    /// <summary>
    /// The statements of a script, separated by <c>;</c>, a blank line or a line with only <c>/</c> (SQL*Plus). Comments
    /// directly above a statement belong to it; parts with only comments are no statement.
    /// </summary>
    public static IReadOnlyList<SqlStatement> Split(string text)
    {
        var statements = new List<SqlStatement>();
        var tokens = Tokenize(text);
        int? start = null;
        var end = 0;
        var hasCode = false;

        void Close()
        {
            if (start is { } s && hasCode)
            {
                statements.Add(new SqlStatement(s, end - s, text[s..end]));
            }

            start = null;
            hasCode = false;
        }

        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (token.Kind == SqlTokenKind.Space)
            {
                if (BlankLine.IsMatch(token.Value))
                {
                    Close();
                }

                continue;
            }

            if (token.IsSymbol(";") || (token.IsSymbol("/") && AloneOnLine(text, token)))
            {
                Close();
                continue;
            }

            start ??= token.Start;
            end = token.End;
            hasCode |= token.Kind != SqlTokenKind.Comment;
        }

        Close();
        return statements;
    }

    /// <summary>
    /// What Ctrl+Enter runs: the selection if it holds more than whitespace, otherwise the statement at the cursor – the
    /// one containing it (or just ending before it on the same line), else the nearest one not separated by a blank line.
    /// </summary>
    public static SqlStatement? StatementAt(string text, int selectionStart, int selectionEnd)
    {
        selectionStart = Math.Clamp(selectionStart, 0, text.Length);
        selectionEnd = Math.Clamp(selectionEnd, selectionStart, text.Length);
        if (selectionEnd > selectionStart && Split(text[selectionStart..selectionEnd]) is { Count: > 0 } selected)
        {
            // A selection of one statement runs as written; one spanning several runs its first and says so elsewhere.
            var first = selected[0];
            return first with { Start = first.Start + selectionStart };
        }

        var statements = Split(text);
        var cursor = selectionStart;
        foreach (var statement in statements)
        {
            if (cursor >= statement.Start && cursor <= statement.End)
            {
                return statement;
            }
        }

        // "SELECT 1 FROM dual;|" or the line below it: the statement just written.
        var before = statements.LastOrDefault(s => s.End <= cursor);
        if (before is not null && !BlankLine.IsMatch(text[before.End..cursor]))
        {
            return before;
        }

        var after = statements.FirstOrDefault(s => s.Start >= cursor);
        return after is not null && !BlankLine.IsMatch(text[cursor..after.Start]) ? after : null;
    }

    /// <summary>How many statements a selection holds.</summary>
    public static int CountStatements(string text) => Split(text).Count;

    /// <summary>
    /// What "Run script" (Alt+X) runs: the statements of the selection if it holds more than whitespace, otherwise
    /// all of the text – with positions in <paramref name="text"/>.
    /// </summary>
    public static IReadOnlyList<SqlStatement> StatementsIn(string text, int selectionStart, int selectionEnd)
    {
        selectionStart = Math.Clamp(selectionStart, 0, text.Length);
        selectionEnd = Math.Clamp(selectionEnd, selectionStart, text.Length);
        if (selectionEnd > selectionStart && Split(text[selectionStart..selectionEnd]) is { Count: > 0 } selected)
        {
            return selected.Select(s => s with { Start = s.Start + selectionStart }).ToList();
        }

        return Split(text);
    }

    public static SqlStatementInfo Analyze(string statement)
    {
        var tokens = Tokenize(statement).Where(t => t.Kind is not (SqlTokenKind.Space or SqlTokenKind.Comment)).ToList();
        if (tokens.Count == 0)
        {
            return new SqlStatementInfo(SqlStatementKind.Empty, "", [], false, false, [], []);
        }

        var first = FirstWordOf(tokens);
        var kind = KindOf(tokens);
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

        return new SqlStatementInfo(kind, first, binds, hasWhere, forUpdate, TablesOf(tokens), BindUsesOf(tokens));
    }

    /// <summary>
    /// What kind of statement the tokens (without spaces and comments, at least one) are – by the first word, for ALTER
    /// and CREATE also by the words after it. Shared with the session guards (<c>StatementGuard</c>).
    /// </summary>
    internal static SqlStatementKind KindOf(IReadOnlyList<SqlToken> tokens)
    {
        var first = FirstWordOf(tokens);
        var second = tokens.Count > 1 && tokens[1].Kind == SqlTokenKind.Word ? tokens[1].Value.ToUpperInvariant() : "";
        return first switch
        {
            "SELECT" or "WITH" => SqlStatementKind.Query,
            "INSERT" => SqlStatementKind.Insert,
            "UPDATE" => SqlStatementKind.Update,
            "DELETE" => SqlStatementKind.Delete,
            "MERGE" => SqlStatementKind.Merge,
            "ALTER" when second is "SESSION" or "SYSTEM" => SqlStatementKind.SessionControl,
            "TRUNCATE" => SqlStatementKind.Truncate,
            "CREATE" when CreatesPlSqlObject(tokens) => SqlStatementKind.PlSqlObject,
            _ when DdlWords.Contains(first) => SqlStatementKind.Ddl,
            "BEGIN" or "DECLARE" => SqlStatementKind.PlSql,
            "CALL" or "EXEC" or "EXECUTE" => SqlStatementKind.Call,
            "COMMIT" or "ROLLBACK" or "SAVEPOINT" or "SET" => SqlStatementKind.TransactionControl,
            "LOCK" => SqlStatementKind.Lock,
            "EXPLAIN" => SqlStatementKind.Explain,
            _ => SqlStatementKind.Unknown,
        };
    }

    /// <summary>
    /// The table a DDL statement changes (WP-22): <c>ALTER TABLE x</c>, <c>DROP TABLE x</c>, <c>TRUNCATE TABLE x</c>,
    /// <c>FLASHBACK TABLE x</c>, <c>RENAME x TO …</c>, <c>CREATE [UNIQUE | BITMAP] INDEX i ON x</c>, <c>COMMENT ON TABLE x</c>,
    /// <c>COMMENT ON COLUMN [owner.]x.c</c> – in dictionary form; null for anything else (a new table, a view, GRANT …).
    /// </summary>
    public static SqlTableReference? DdlTableOf(string statement)
    {
        var tokens = Tokenize(statement).Where(t => t.Kind is not (SqlTokenKind.Space or SqlTokenKind.Comment)).ToList();
        if (tokens.Count < 2 || KindOf(tokens) is not (SqlStatementKind.Ddl or SqlStatementKind.Truncate))
        {
            return null;
        }

        var first = FirstWordOf(tokens);
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

        return at is { } i && ReferenceAt(tokens, i, 0) is { } reference ? reference.Table with { Alias = null } : null;
    }

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
            if (Name(tokens, i) is not { } name)
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

    private static string FirstWordOf(IReadOnlyList<SqlToken> tokens) =>
        tokens[0].Kind == SqlTokenKind.Word ? tokens[0].Value.ToUpperInvariant() : tokens[0].Value;

    /// <summary>
    /// <c>CREATE [OR REPLACE] [EDITIONABLE | NONEDITIONABLE] PROCEDURE …</c> and the other objects with a PL/SQL (or Java)
    /// body. Their body holds <c>;</c>, so the editor's split would cut them apart anyway.
    /// </summary>
    private static bool CreatesPlSqlObject(IReadOnlyList<SqlToken> tokens)
    {
        for (var i = 1; i < tokens.Count && tokens[i].Kind == SqlTokenKind.Word; i++)
        {
            var word = tokens[i].Value.ToUpperInvariant();
            if (PlSqlObjects.Contains(word))
            {
                return true;
            }

            if (!CreateModifiers.Contains(word))
            {
                return false;
            }
        }

        return false;
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

    private static bool AloneOnLine(string text, SqlToken token)
    {
        var lineStart = token.Start == 0 ? 0 : text.LastIndexOf('\n', token.Start - 1) + 1;
        var lineEnd = text.IndexOf('\n', token.End);
        lineEnd = lineEnd < 0 ? text.Length : lineEnd;
        return text[lineStart..token.Start].Trim().Length == 0 && text[token.End..lineEnd].Trim().Length == 0;
    }

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
