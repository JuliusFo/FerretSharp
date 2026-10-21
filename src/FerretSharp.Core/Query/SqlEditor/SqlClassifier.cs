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

    /// <summary>
    /// DDL that deletes or overwrites rows without a way back – never run (decision of the user, WP-22): TRUNCATE, and since
    /// review 3.31.0 (A9) <c>ALTER TABLE … TRUNCATE/DROP [SUB]PARTITION</c>, <c>FLASHBACK TABLE … TO</c> a time, SCN or
    /// restore point (not <c>TO BEFORE DROP</c>, which brings a dropped table back) and <c>PURGE</c>.
    /// </summary>
    Truncate,

    /// <summary>CREATE [OR REPLACE] PROCEDURE, FUNCTION, PACKAGE, TYPE, JAVA: DDL with a PL/SQL body.</summary>
    PlSqlObject,

    /// <summary>
    /// CREATE [OR REPLACE] TRIGGER on a table or view (a DML trigger): DDL with a PL/SQL body that the schema path takes
    /// (WP-26, ADR 0021). <c>:new</c> and <c>:old</c> in it are no bind variables.
    /// </summary>
    Trigger,

    /// <summary>CREATE [OR REPLACE] TRIGGER … ON DATABASE or ON SCHEMA: fires on logon or on every DDL – never run (ADR 0021).</summary>
    SystemTrigger,
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
            // ALTER DATABASE / PLUGGABLE DATABASE (CLOSE IMMEDIATE …) like ALTER SYSTEM (review 3.44.0, A14, decision of the user).
            "ALTER" when second is "SESSION" or "SYSTEM" or "DATABASE" or "PLUGGABLE" => SqlStatementKind.SessionControl,
            "TRUNCATE" or "PURGE" => SqlStatementKind.Truncate,
            "ALTER" when second == "TABLE" && DropsPartition(tokens) => SqlStatementKind.Truncate,
            "FLASHBACK" when !Follows(tokens, "TO", "BEFORE", "DROP") => SqlStatementKind.Truncate,
            // Decision of the user (review 3.40.0, SE-8): past the recycle bin, and a schema with all its objects.
            "DROP" when second == "TABLE" && Follows(tokens, "PURGE") => SqlStatementKind.Truncate,
            "DROP" when second == "USER" && Follows(tokens, "CASCADE") => SqlStatementKind.Truncate,
            // The same past the recycle bin (review 3.44.0, A14, decision of the user): a tablespace with its tables, a cluster
            // with its tables, a pluggable database with its data files.
            "DROP" when second == "TABLESPACE" && Follows(tokens, "INCLUDING", "CONTENTS") => SqlStatementKind.Truncate,
            "DROP" when second == "CLUSTER" && Follows(tokens, "INCLUDING", "TABLES") => SqlStatementKind.Truncate,
            "DROP" when second == "PLUGGABLE" && Follows(tokens, "INCLUDING", "DATAFILES") => SqlStatementKind.Truncate,
            "CREATE" when PlSqlObjectAt(tokens) is { } at => !tokens[at].IsWord("TRIGGER") ? SqlStatementKind.PlSqlObject
                : IsSystemTrigger(tokens, at) ? SqlStatementKind.SystemTrigger
                : SqlStatementKind.Trigger,
            _ when DdlWords.Contains(first) => SqlStatementKind.Ddl,
            "BEGIN" or "DECLARE" => SqlStatementKind.PlSql,
            "CALL" or "EXEC" or "EXECUTE" => SqlStatementKind.Call,
            "COMMIT" or "ROLLBACK" or "SAVEPOINT" or "SET" => SqlStatementKind.TransactionControl,
            "LOCK" => SqlStatementKind.Lock,
            "EXPLAIN" => SqlStatementKind.Explain,
            _ => SqlStatementKind.Unknown,
        };
    }

    /// <summary><c>TRUNCATE</c> or <c>DROP</c> of partitions anywhere in an ALTER TABLE: the rows in them are gone.</summary>
    private static bool DropsPartition(IReadOnlyList<SqlToken> tokens)
    {
        for (var i = 2; i + 1 < tokens.Count; i++)
        {
            if ((tokens[i].IsWord("TRUNCATE") || tokens[i].IsWord("DROP"))
                && (tokens[i + 1].IsWord("PARTITION") || tokens[i + 1].IsWord("SUBPARTITION")
                    || tokens[i + 1].IsWord("PARTITIONS") || tokens[i + 1].IsWord("SUBPARTITIONS")))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether the words follow each other somewhere in the statement.</summary>
    private static bool Follows(IReadOnlyList<SqlToken> tokens, params string[] words)
    {
        for (var i = 0; i + words.Length <= tokens.Count; i++)
        {
            if (words.Select((word, j) => tokens[i + j].IsWord(word)).All(match => match))
            {
                return true;
            }
        }

        return false;
    }

    internal static string FirstWordOf(IReadOnlyList<SqlToken> tokens) =>
        tokens[0].Kind == SqlTokenKind.Word ? tokens[0].Value.ToUpperInvariant() : tokens[0].Value;

    /// <summary>
    /// Where <c>CREATE [OR REPLACE] [EDITIONABLE | NONEDITIONABLE] PROCEDURE …</c> and the other objects with a PL/SQL (or
    /// Java) body name their object type; null for any other CREATE. Their body holds <c>;</c>, so the editor's split would
    /// cut them apart anyway.
    /// </summary>
    private static int? PlSqlObjectAt(IReadOnlyList<SqlToken> tokens)
    {
        for (var i = 1; i < tokens.Count && tokens[i].Kind == SqlTokenKind.Word; i++)
        {
            var word = tokens[i].Value.ToUpperInvariant();
            if (PlSqlObjects.Contains(word))
            {
                return i;
            }

            if (!CreateModifiers.Contains(word))
            {
                return null;
            }
        }

        return null;
    }

    /// <summary>
    /// A trigger on the database or a schema (<c>AFTER LOGON ON DATABASE</c>, <c>BEFORE DDL ON [owner.]SCHEMA</c>,
    /// <c>ON PLUGGABLE DATABASE</c>) rather than on a table or view: the first <c>ON</c> after the trigger's name decides.
    /// A quoted <c>"SCHEMA"</c> is a table of that name.
    /// </summary>
    private static bool IsSystemTrigger(IReadOnlyList<SqlToken> tokens, int trigger)
    {
        for (var i = trigger + 1; i + 1 < tokens.Count; i++)
        {
            if (!tokens[i].IsWord("ON"))
            {
                continue;
            }

            var target = tokens[i + 1];
            return target.IsWord("DATABASE") || target.IsWord("PLUGGABLE") || target.IsWord("SCHEMA")
                || (i + 3 < tokens.Count && tokens[i + 2].IsSymbol(".") && tokens[i + 3].IsWord("SCHEMA"));
        }

        return false;
    }
}
