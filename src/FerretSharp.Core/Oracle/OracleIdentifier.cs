using System.Text;
using System.Text.RegularExpressions;
using FerretSharp.Core.Resources;

namespace FerretSharp.Core.Oracle;

public static partial class OracleIdentifier
{
    /// <summary>Oracle's limit for names since 12.2: 128 bytes (UTF-8), not characters.</summary>
    public const int MaxBytes = 128;

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

    /// <summary>
    /// What keeps a typed name from being created exactly as written (FerretSharp always quotes it, <see cref="Quote"/>): a
    /// double quote or NUL, or more than <see cref="MaxBytes"/> bytes – as a message, null if neither. An empty name is the
    /// caller's to report, each says what is missing (R5b: before, six checks with their own rules).
    /// </summary>
    public static string? Problem(string name) =>
        name.Contains('"', StringComparison.Ordinal) || name.Contains('\0', StringComparison.Ordinal) ? TextFormat.Format(CoreText.NameInvalid, name)
        : Encoding.UTF8.GetByteCount(name) > MaxBytes ? TextFormat.Format(CoreText.NameTooLong, name, MaxBytes)
        : null;

    /// <summary>
    /// A typed name SQL and code must write in double quotes, exactly so – for a warning: not upper case letters (of any
    /// script, as Oracle takes them unquoted), digits, <c>_ $ #</c> starting with a letter, or a reserved word (<c>DATE</c>,
    /// <c>END</c>). Unlike <see cref="IsPlain"/>, which quotes on the safe side when FerretSharp writes a name.
    /// </summary>
    public static bool NeedsQuotes(string name) => !PlainForOracle().IsMatch(name) || Reserved.Contains(name);

    /// <summary>The longest start of <paramref name="text"/> within <paramref name="maxBytes"/> UTF-8 bytes, never half a character.</summary>
    public static string Cut(string text, int maxBytes = MaxBytes)
    {
        var length = text.Length;
        while (Encoding.UTF8.GetByteCount(text.AsSpan(0, length)) > maxBytes)
        {
            length--;
        }

        if (length > 0 && length < text.Length && char.IsHighSurrogate(text[length - 1]))
        {
            length--; // never half a character
        }

        return text[..length];
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

    /// <summary>Owner and name as code a user reads (<see cref="QuoteIfNeeded"/>): <c>APP.KUNDEN</c>, <c>APP."Kunden"</c>.</summary>
    public static string QualifyIfNeeded(string owner, string name) => $"{QuoteIfNeeded(owner)}.{QuoteIfNeeded(name)}";

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

    // \z, not $: $ also matches before a final line break – "KUNDEN\n" (a quoted name) would count as plain (review of WP-70).
    [GeneratedRegex(@"^[A-Z][A-Z0-9_$#]*\z", RegexOptions.CultureInvariant)]
    private static partial Regex PlainName();

    /// <summary>A name Oracle takes without quotes: a letter, then letters, digits, _, $ and #, upper case (any script).</summary>
    [GeneratedRegex(@"^\p{Lu}[\p{Lu}\p{Nd}_$#]*\z")]
    private static partial Regex PlainForOracle();
}
