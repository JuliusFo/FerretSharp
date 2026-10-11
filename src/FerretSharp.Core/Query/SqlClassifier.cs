namespace FerretSharp.Core.Query;

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

/// <summary>
/// What kind of statement it is, by its first words (part of <see cref="SqlScript"/> since R4b). Shared by the SQL editor
/// and the session's statement guards (<c>StatementGuard</c>, ADR 0006/0019) – what the editor runs as DDL is exactly what
/// the schema path takes.
/// </summary>
public static class SqlClassifier
{
    private static readonly HashSet<string> DdlWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "CREATE", "ALTER", "DROP", "RENAME", "COMMENT", "GRANT", "REVOKE", "ANALYZE", "AUDIT", "NOAUDIT", "FLASHBACK", "PURGE",
        "ASSOCIATE", "DISASSOCIATE",
    };

    /// <summary>
    /// Objects whose CREATE carries a PL/SQL body or other stored code: Java, JavaScript (<c>MLE MODULE</c>/<c>ENV</c>, 23ai,
    /// valid without any <c>;</c>), external libraries.
    /// </summary>
    private static readonly HashSet<string> PlSqlObjects = new(StringComparer.OrdinalIgnoreCase)
    {
        "PROCEDURE", "FUNCTION", "PACKAGE", "TRIGGER", "TYPE", "JAVA", "MLE", "LIBRARY",
    };

    /// <summary>Words between CREATE and the object type: <c>OR REPLACE</c>, <c>EDITIONABLE</c>, <c>AND COMPILE</c> (Java) ….</summary>
    private static readonly HashSet<string> CreateModifiers = new(StringComparer.OrdinalIgnoreCase)
    {
        "OR", "REPLACE", "EDITIONABLE", "NONEDITIONABLE", "AND", "COMPILE", "RESOLVE", "NOFORCE",
    };

    /// <summary>What kind of statement <paramref name="statement"/> is; <see cref="SqlStatementKind.Empty"/> without code.</summary>
    public static SqlStatementKind KindOf(string statement) =>
        SqlTokenizer.Significant(statement) is { Count: > 0 } tokens ? KindOf(tokens) : SqlStatementKind.Empty;

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

    internal static string FirstWordOf(IReadOnlyList<SqlToken> tokens) =>
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
}
