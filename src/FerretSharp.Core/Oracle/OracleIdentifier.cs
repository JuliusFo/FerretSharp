using System.Text.RegularExpressions;

namespace FerretSharp.Core.Oracle;

public static partial class OracleIdentifier
{
    /// <summary>
    /// Reserved words of SQL (<c>V$RESERVED_WORDS</c> with RESERVED = 'Y') and of PL/SQL (PL/SQL Language Reference,
    /// "PL/SQL Reserved Words"): a name like these needs quotes. Both lists, so a name written plainly works in SQL and in a
    /// trigger body alike; a name quoted without need is still exact.
    /// </summary>
    private static readonly HashSet<string> Reserved = new(StringComparer.Ordinal)
    {
        // SQL
        "ACCESS", "ADD", "ALL", "ALTER", "AND", "ANY", "AS", "ASC", "AUDIT", "BETWEEN", "BY", "CHAR", "CHECK", "CLUSTER", "COLUMN",
        "COMMENT", "COMPRESS", "CONNECT", "CREATE", "CURRENT", "DATE", "DECIMAL", "DEFAULT", "DELETE", "DESC", "DISTINCT", "DROP",
        "ELSE", "EXCLUSIVE", "EXISTS", "FILE", "FLOAT", "FOR", "FROM", "GRANT", "GROUP", "HAVING", "IDENTIFIED", "IMMEDIATE", "IN",
        "INCREMENT", "INDEX", "INITIAL", "INSERT", "INTEGER", "INTERSECT", "INTO", "IS", "LEVEL", "LIKE", "LOCK", "LONG", "MAXEXTENTS",
        "MINUS", "MLSLABEL", "MODE", "MODIFY", "NOAUDIT", "NOCOMPRESS", "NOT", "NOWAIT", "NULL", "NUMBER", "OF", "OFFLINE", "ON",
        "ONLINE", "OPTION", "OR", "ORDER", "PCTFREE", "PRIOR", "PUBLIC", "RAW", "RENAME", "RESOURCE", "REVOKE", "ROW", "ROWID",
        "ROWNUM", "ROWS", "SELECT", "SESSION", "SET", "SHARE", "SIZE", "SMALLINT", "START", "SUCCESSFUL", "SYNONYM", "SYSDATE",
        "TABLE", "THEN", "TO", "TRIGGER", "UID", "UNION", "UNIQUE", "UPDATE", "USER", "VALIDATE", "VALUES", "VARCHAR", "VARCHAR2",
        "VIEW", "WHENEVER", "WHERE", "WITH",

        // PL/SQL, as far as not above
        "AT", "BEGIN", "CASE", "CLUSTERS", "COLAUTH", "COLUMNS", "CRASH", "CURSOR", "DECLARE", "END", "EXCEPTION", "FETCH",
        "FUNCTION", "GOTO", "IF", "INDEXES", "OVERLAPS", "PROCEDURE", "SQL", "SUBTYPE", "TABAUTH", "TYPE", "VIEWS", "WHEN",
    };

    /// <summary>
    /// Quotes a name exactly as stored in the data dictionary, so mixed-case and special names work:
    /// <c>KUNDEN</c> → <c>"KUNDEN"</c>, <c>MixedCase</c> → <c>"MixedCase"</c>.
    /// </summary>
    public static string Quote(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        if (name.Contains('"', StringComparison.Ordinal) || name.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException($"Oracle identifiers cannot contain double quotes or NUL: {name}", nameof(name));
        }

        return $"\"{name}\"";
    }

    /// <summary><c>"OWNER"."NAME"</c>.</summary>
    public static string Qualify(string owner, string name) => $"{Quote(owner)}.{Quote(name)}";

    /// <summary>
    /// Oracle reads the name as written without quotes: upper case letters, digits, <c>_ $ #</c>, starting with a letter,
    /// and not a reserved word of SQL or PL/SQL.
    /// </summary>
    public static bool IsPlain(string name) => PlainName().IsMatch(name) && !Reserved.Contains(name);

    /// <summary>
    /// A name as code a user reads: plain if Oracle reads it as written (<see cref="IsPlain"/>), quoted exactly otherwise
    /// (<c>KUNDEN</c> → <c>KUNDEN</c>, <c>Kunden</c> → <c>"Kunden"</c>, <c>DATE</c> → <c>"DATE"</c>).
    /// </summary>
    public static string QuoteIfNeeded(string name) => IsPlain(name) ? name : Quote(name);

    /// <summary>
    /// Converts a name as a user would type it into the dictionary form: unquoted names are upper-cased
    /// (<c>erp</c> → <c>ERP</c>), quoted names keep their case (<c>"Erp"</c> → <c>Erp</c>).
    /// </summary>
    public static string Normalize(string typed)
    {
        var trimmed = typed.Trim();
        return trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[^1] == '"'
            ? trimmed[1..^1]
            : trimmed.ToUpperInvariant();
    }

    [GeneratedRegex("^[A-Z][A-Z0-9_$#]*$", RegexOptions.CultureInvariant)]
    private static partial Regex PlainName();
}
