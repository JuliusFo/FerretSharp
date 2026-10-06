using System.Text.RegularExpressions;
using FerretSharp.Core.ClrModel;
using FerretSharp.Core.Schema;

namespace FerretSharp.Core.Query;

public enum SqlCompletionKind
{
    Table,
    View,
    Synonym,
    Column,
    Keyword,
}

/// <param name="Label">What the list shows (<c>KUNDEN</c>, <c>KUNDE_ID</c>).</param>
/// <param name="InsertText">What goes into the editor: the name, quoted where Oracle needs it (<c>"Auftrag"</c>, <c>"DATE"</c>), maybe with a space after it.</param>
/// <param name="Detail">Type and C# names: <c>NUMBER(10) · KundeId · int</c>, <c>Tabelle · Entity Kunde</c>.</param>
/// <param name="Rank">Lower comes first: columns of the table before the dot, then other columns, tables, keywords.</param>
public sealed record SqlCompletionItem(string Label, string InsertText, SqlCompletionKind Kind, string? Detail, int Rank);

/// <summary>
/// Completion for the SQL editor (WP-17) from the schema cache: tables, views and synonyms after FROM, JOIN, INTO, UPDATE,
/// USING (and the commas of a FROM list); the columns of a table or alias after <c>name.</c>; otherwise the columns of all
/// tables the statement names, then keywords. Column lists come lazily from the cache (one dictionary query per table).
/// </summary>
public static class SqlCompletion
{
    private static readonly Regex PlainName = new("^[A-Z][A-Z0-9_$#]*$", RegexOptions.CultureInvariant);

    /// <summary>Oracle's reserved words (<c>V$RESERVED_WORDS</c> with RESERVED = 'Y'): names like these need quotes.</summary>
    private static readonly HashSet<string> Reserved = new(StringComparer.Ordinal)
    {
        "ACCESS", "ADD", "ALL", "ALTER", "AND", "ANY", "AS", "ASC", "AUDIT", "BETWEEN", "BY", "CHAR", "CHECK", "CLUSTER", "COLUMN",
        "COMMENT", "COMPRESS", "CONNECT", "CREATE", "CURRENT", "DATE", "DECIMAL", "DEFAULT", "DELETE", "DESC", "DISTINCT", "DROP",
        "ELSE", "EXCLUSIVE", "EXISTS", "FILE", "FLOAT", "FOR", "FROM", "GRANT", "GROUP", "HAVING", "IDENTIFIED", "IMMEDIATE", "IN",
        "INCREMENT", "INDEX", "INITIAL", "INSERT", "INTEGER", "INTERSECT", "INTO", "IS", "LEVEL", "LIKE", "LOCK", "LONG", "MAXEXTENTS",
        "MINUS", "MLSLABEL", "MODE", "MODIFY", "NOAUDIT", "NOCOMPRESS", "NOT", "NOWAIT", "NULL", "NUMBER", "OF", "OFFLINE", "ON",
        "ONLINE", "OPTION", "OR", "ORDER", "PCTFREE", "PRIOR", "PUBLIC", "RAW", "RENAME", "RESOURCE", "REVOKE", "ROW", "ROWID",
        "ROWNUM", "ROWS", "SELECT", "SESSION", "SET", "SHARE", "SIZE", "SMALLINT", "START", "SUCCESSFUL", "SYNONYM", "SYSDATE",
        "TABLE", "THEN", "TO", "TRIGGER", "UID", "UNION", "UNIQUE", "UPDATE", "USER", "VALIDATE", "VALUES", "VARCHAR", "VARCHAR2",
        "VIEW", "WHENEVER", "WHERE", "WITH",
    };

    private static readonly string[] Keywords =
    [
        "SELECT", "FROM", "WHERE", "AND", "OR", "NOT", "NULL", "IS NULL", "IS NOT NULL", "IN", "EXISTS", "BETWEEN", "LIKE", "JOIN",
        "LEFT JOIN", "INNER JOIN", "ON", "GROUP BY", "HAVING", "ORDER BY", "ASC", "DESC", "DISTINCT", "COUNT(*)", "CASE", "WHEN",
        "THEN", "ELSE", "END", "UNION ALL", "FETCH FIRST 100 ROWS ONLY", "OFFSET", "INSERT INTO", "VALUES", "UPDATE", "SET",
        "DELETE FROM", "MERGE INTO", "USING", "WITH", "SYSDATE", "SYSTIMESTAMP", "TRUNC", "NVL", "TO_DATE", "TO_CHAR", "UPPER",
    ];

    /// <summary>Keywords that are values or functions: a comma, a parenthesis or an operator usually follows, not a space.</summary>
    private static readonly HashSet<string> ValueKeywords = new(StringComparer.Ordinal)
    {
        "NULL", "ASC", "DESC", "END", "COUNT(*)", "FETCH FIRST 100 ROWS ONLY", "SYSDATE", "SYSTIMESTAMP", "TRUNC", "NVL", "TO_DATE",
        "TO_CHAR", "UPPER",
    };

    private static readonly HashSet<string> TableContext = new(StringComparer.OrdinalIgnoreCase)
    {
        "FROM", "JOIN", "INTO", "UPDATE", "USING", "TABLE",
    };

    /// <summary>Words that decide whether a comma separates tables (after FROM) or something else.</summary>
    private static readonly HashSet<string> ClauseWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "FROM", "WHERE", "SELECT", "GROUP", "ORDER", "HAVING", "SET", "ON", "VALUES", "BY", "RETURNING",
    };

    /// <param name="statement">The statement around the cursor (the one Ctrl+Enter would run).</param>
    /// <param name="offset">Cursor position in <paramref name="statement"/>.</param>
    /// <param name="atLineEnd">
    /// Nothing follows the cursor on its line (<see cref="AtLineEnd"/>): tables and clause keywords then end with a space, so
    /// typing goes on with the alias or the next word. Columns never do – a comma, parenthesis or operator usually follows.
    /// </param>
    /// <param name="present">The presentation of a table (C# names); <see cref="TablePresentation.Plain"/> without a model.</param>
    /// <param name="entityOf">The entity name of a table, for the detail of table items; null without one.</param>
    public static async Task<IReadOnlyList<SqlCompletionItem>> ItemsAsync(
        string statement, int offset, bool atLineEnd, SchemaCache schema, Func<TableDetails, TablePresentation> present,
        Func<TableRef, string?> entityOf, CancellationToken cancellationToken)
    {
        var items = await ItemsAsync(statement, offset, schema, present, entityOf, cancellationToken);
        return atLineEnd
            ? items.Select(i => i.Kind != SqlCompletionKind.Column && !ValueKeywords.Contains(i.InsertText) ? i with { InsertText = i.InsertText + " " } : i).ToList()
            : items;
    }

    /// <summary>The cursor is at the end of the text or of its line – already followed by a space, a comma or a word, a completion needs no space.</summary>
    public static bool AtLineEnd(string text, int cursor) => cursor >= text.Length || text[cursor] is '\r' or '\n';

    private static async Task<IReadOnlyList<SqlCompletionItem>> ItemsAsync(
        string statement, int offset, SchemaCache schema, Func<TableDetails, TablePresentation> present, Func<TableRef, string?> entityOf,
        CancellationToken cancellationToken)
    {
        offset = Math.Clamp(offset, 0, statement.Length);
        if (InsideCommentOrLiteral(statement, offset))
        {
            return [];
        }

        var tokens = SqlScript.Tokenize(statement[..offset]).Where(t => t.Kind is not (SqlTokenKind.Space or SqlTokenKind.Comment)).ToList();
        var i = tokens.Count - 1;
        if (i >= 0 && tokens[i].Kind is SqlTokenKind.Word or SqlTokenKind.QuotedIdentifier && tokens[i].End == offset)
        {
            i--; // the word being typed: Monaco filters by it
        }

        var tables = SqlScript.Analyze(statement).Tables;
        if (i >= 1 && tokens[i].IsSymbol(".") && tokens[i - 1].Kind is SqlTokenKind.Word or SqlTokenKind.QuotedIdentifier)
        {
            var qualifier = Normalize(tokens[i - 1]);
            var reference = tables.FirstOrDefault(t => t.Alias == qualifier) ?? tables.FirstOrDefault(t => t.Alias is null && t.Name == qualifier);
            if (reference is not null && Resolve(schema, reference) is { } table)
            {
                return await ColumnsAsync([table], schema, present, 0, cancellationToken);
            }

            return schema.Tables.Where(t => t.Owner == qualifier && t.Synonym is null)
                .Select(t => TableItem(t, Quote(t.Name), t.Name, entityOf)).ToList();
        }

        if (i >= 0 && (tokens[i].Kind == SqlTokenKind.Word && TableContext.Contains(tokens[i].Value)
                       || tokens[i].IsSymbol(",") && InFromList(tokens, i)))
        {
            return Tables(schema, entityOf);
        }

        var referenced = tables.Select(t => Resolve(schema, t)).OfType<TableSummary>().DistinctBy(t => t.Ref).ToList();
        var items = new List<SqlCompletionItem>(await ColumnsAsync(referenced, schema, present, 1, cancellationToken));
        if (referenced.Count == 0)
        {
            items.AddRange(Tables(schema, entityOf));
        }

        items.AddRange(Keywords.Select(k => new SqlCompletionItem(k, k, SqlCompletionKind.Keyword, null, 4)));
        return items;
    }

    /// <summary>The object a reference names: own table or view, an object of the owner given, or a synonym of that name.</summary>
    public static TableSummary? Resolve(SchemaCache schema, SqlTableReference reference) =>
        reference.Owner is { } owner
            ? schema.Find(new TableRef(owner, reference.Name)) ?? schema.Tables.FirstOrDefault(t => t.Owner == owner && t.Name == reference.Name)
            : schema.Find(new TableRef(schema.Owner, reference.Name)) ?? schema.Tables.FirstOrDefault(t => t.DisplayName == reference.Name);

    /// <summary>A name as SQL: plain if Oracle reads it as written, quoted otherwise (<c>MixedCase</c>, reserved words).</summary>
    public static string Quote(string name) => PlainName.IsMatch(name) && !Reserved.Contains(name) ? name : $"\"{name}\"";

    private static List<SqlCompletionItem> Tables(SchemaCache schema, Func<TableRef, string?> entityOf) =>
        schema.Tables.Select(t => t.Synonym is not null || t.Owner == schema.Owner
                ? TableItem(t, Quote(t.DisplayName), t.DisplayName, entityOf)
                : TableItem(t, $"{Quote(t.Owner)}.{Quote(t.Name)}", $"{t.Owner}.{t.Name}", entityOf))
            .ToList();

    private static SqlCompletionItem TableItem(TableSummary table, string insert, string label, Func<TableRef, string?> entityOf)
    {
        var (kind, text) = table switch
        {
            { Synonym: { } synonym } => (SqlCompletionKind.Synonym, $"Synonym → {table.Owner}.{table.Name}{(synonym.IsPublic ? " (öffentlich)" : "")}"),
            { Kind: TableKind.View } => (SqlCompletionKind.View, "View"),
            { Kind: TableKind.MaterializedView } => (SqlCompletionKind.View, "Materialized View"),
            _ => (SqlCompletionKind.Table, "Tabelle"),
        };
        var entity = entityOf(table.Ref) is { } name ? $" · Entity {name}" : "";
        return new SqlCompletionItem(label, insert, kind, text + entity, 2);
    }

    private static async Task<List<SqlCompletionItem>> ColumnsAsync(
        IReadOnlyList<TableSummary> tables, SchemaCache schema, Func<TableDetails, TablePresentation> present, int rank,
        CancellationToken cancellationToken)
    {
        var items = new List<SqlCompletionItem>();
        foreach (var table in tables)
        {
            var details = await schema.GetDetailsAsync(table, cancellationToken);
            var presentation = present(details);
            for (var c = 0; c < details.Columns.Count; c++)
            {
                var column = details.Columns[c];
                var property = presentation.PropertyOf(c) is { } p ? $" · {p.Name} · {TablePresentation.ClrTypeText(p)}" : "";
                var from = tables.Count > 1 ? $"{table.DisplayName} · " : "";
                items.Add(new SqlCompletionItem(column.Name, Quote(column.Name), SqlCompletionKind.Column, from + column.DisplayType + property, rank));
            }
        }

        return items;
    }

    /// <summary>A comma whose nearest clause word (at the same parenthesis level) is FROM separates tables.</summary>
    private static bool InFromList(List<SqlToken> tokens, int i)
    {
        var depth = 0;
        for (var j = i - 1; j >= 0; j--)
        {
            if (tokens[j].IsSymbol(")"))
            {
                depth++;
            }
            else if (tokens[j].IsSymbol("("))
            {
                if (depth == 0)
                {
                    return false;
                }

                depth--;
            }
            else if (depth == 0 && tokens[j].Kind == SqlTokenKind.Word && ClauseWords.Contains(tokens[j].Value))
            {
                return tokens[j].IsWord("FROM");
            }
        }

        return false;
    }

    /// <summary>
    /// The cursor is in a comment or string: text typed there would become part of it. Probed by appending a word – a
    /// literal closed before the cursor does not swallow it, an open one (or a line comment) does.
    /// </summary>
    private static bool InsideCommentOrLiteral(string statement, int offset) =>
        SqlScript.Tokenize(statement[..offset] + " x")
            .Any(t => t.Kind is SqlTokenKind.Comment or SqlTokenKind.Text && t.Start < offset && t.End > offset);

    private static string Normalize(SqlToken token) =>
        token.Kind == SqlTokenKind.QuotedIdentifier ? token.Value : token.Value.ToUpperInvariant();
}
