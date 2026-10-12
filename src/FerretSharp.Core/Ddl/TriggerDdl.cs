using FerretSharp.Core.Query;

namespace FerretSharp.Core.Ddl;

/// <summary>
/// Text helpers for creating a trigger safely (WP-26, ADR 0021): first <c>DISABLE</c>, then – only if it compiled –
/// <c>ALTER TRIGGER … ENABLE</c>. Built on the SQL editor's tokenizer, not a parser: it reads the header of a
/// <c>CREATE [OR REPLACE] TRIGGER</c> (<see cref="SqlStatementKind.Trigger"/>) up to where the body starts.
/// </summary>
public static class TriggerDdl
{
    /// <summary>
    /// <paramref name="createTrigger"/> with <c>DISABLE</c> in its header, where Oracle expects it: after timing, events,
    /// <c>ON</c>, <c>REFERENCING</c>, <c>FOR EACH ROW</c>, <c>FOLLOWS</c>/<c>PRECEDES</c>, before the first top-level
    /// <c>WHEN</c>, <c>DECLARE</c>, <c>BEGIN</c>, <c>COMPOUND</c>, <c>CALL</c> or <c>&lt;&lt;label&gt;&gt;</c>. It goes right
    /// behind the last word of the header, so no line of the body moves (compile errors keep their line and column). The
    /// rest of the text stays as it is.
    /// </summary>
    /// <returns>
    /// Null if the statement is no trigger on a table or view, if its header already says <c>ENABLE</c> or <c>DISABLE</c>
    /// (the user's choice stays, and nothing is enabled afterwards), or if its body cannot be found.
    /// </returns>
    public static string? WithDisable(string createTrigger)
    {
        ArgumentNullException.ThrowIfNull(createTrigger);
        return Header(createTrigger) is { Stated: null, InsertAt: var at }
            ? string.Concat(createTrigger.AsSpan(0, at), " DISABLE", createTrigger.AsSpan(at))
            : null;
    }

    /// <summary>
    /// What the header of a <c>CREATE TRIGGER</c> says about its state: true for <c>ENABLE</c>, false for <c>DISABLE</c>,
    /// null if it says nothing (Oracle creates it enabled) or the statement is no trigger on a table or view.
    /// </summary>
    public static bool? StatedEnabled(string createTrigger) => Header(createTrigger)?.Stated;

    /// <summary>
    /// The name of the trigger a <c>CREATE [OR REPLACE] TRIGGER</c> creates, in dictionary form (unquoted → upper case,
    /// quoted → as written), with its owner if the statement names one; null for any other statement.
    /// </summary>
    public static (string? Owner, string Name)? NameOf(string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);
        var tokens = SqlTokenizer.Significant(sql);
        return NameAt(tokens) is { } name ? (name.Owner, name.Name) : null;
    }

    /// <summary><c>ALTER TRIGGER "OWNER"."NAME" ENABLE</c>: switches on a trigger that compiled.</summary>
    public static string Enable(string owner, string name) => OracleDdl.EnableTrigger(owner, name);

    /// <summary>The trigger's name (and owner) and the index of the first token after it; null if this is no trigger.</summary>
    private static (string? Owner, string Name, int Next)? NameAt(List<SqlToken> tokens)
    {
        if (tokens.Count == 0 || SqlClassifier.KindOf(tokens) != SqlStatementKind.Trigger)
        {
            return null;
        }

        // The classifier found TRIGGER among CREATE's modifiers (OR REPLACE, EDITIONABLE …): the first one is the object type.
        var i = tokens.FindIndex(t => t.IsWord("TRIGGER")) + 1;
        if (i + 2 < tokens.Count && tokens[i].IsWord("IF") && tokens[i + 1].IsWord("NOT") && tokens[i + 2].IsWord("EXISTS"))
        {
            i += 3;
        }

        if (NameOf(tokens, i) is not { } first)
        {
            return null;
        }

        return i + 2 < tokens.Count && tokens[i + 1].IsSymbol(".") && NameOf(tokens, i + 2) is { } second
            ? (first, second, i + 3)
            : (null, first, i + 1);
    }

    private static string? NameOf(List<SqlToken> tokens, int i) => i < tokens.Count ? tokens[i].Kind switch
    {
        SqlTokenKind.Word => tokens[i].Value.ToUpperInvariant(),
        SqlTokenKind.QuotedIdentifier => tokens[i].Value,
        _ => null,
    } : null;

    /// <summary>
    /// Reads the header: whether it says ENABLE/DISABLE, and where DISABLE would go (the end of its last token). Null if
    /// this is no trigger or its body cannot be found.
    /// </summary>
    private static (bool? Stated, int InsertAt)? Header(string sql)
    {
        var tokens = SqlTokenizer.Significant(sql);
        if (NameAt(tokens) is not { Next: var start })
        {
            return null;
        }

        bool? stated = null;
        var depth = 0;
        for (var i = start; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (token.IsSymbol("("))
            {
                depth++;
                continue;
            }

            if (token.IsSymbol(")"))
            {
                depth = Math.Max(0, depth - 1);
                continue;
            }

            if (depth > 0)
            {
                continue;
            }

            if (token.IsWord("ENABLE") || token.IsWord("DISABLE"))
            {
                stated = token.IsWord("ENABLE");
            }
            else if (IsBodyStart(tokens, i))
            {
                return (stated, tokens[i - 1].End);
            }
        }

        return null;
    }

    /// <summary>
    /// Where a compile error of the trigger is in <paramref name="createTrigger"/> (offset into the text); null if the error
    /// lies outside it. Oracle counts a trigger's error lines from its body – the line of DECLARE/BEGIN is line 1 (WP-26,
    /// experiment against Oracle 23), on that line the column from the body's start, on later lines from the line's start.
    /// </summary>
    public static int? ErrorOffset(string createTrigger, int line, int column)
    {
        ArgumentNullException.ThrowIfNull(createTrigger);
        if (BodyStart(createTrigger) is not { } body || line < 1 || column < 1)
        {
            return null;
        }

        var offset = body;
        for (var n = 1; n < line; n++)
        {
            offset = createTrigger.IndexOf('\n', offset) + 1;
            if (offset == 0)
            {
                return null;
            }
        }

        offset += column - 1;
        return offset < createTrigger.Length ? offset : null;
    }

    /// <summary>The offset of the body (DECLARE, BEGIN, a label, COMPOUND, CALL) – past a WHEN condition; null if there is none.</summary>
    private static int? BodyStart(string sql)
    {
        var tokens = SqlTokenizer.Significant(sql);
        if (NameAt(tokens) is not { Next: var start })
        {
            return null;
        }

        var depth = 0;
        for (var i = start; i < tokens.Count; i++)
        {
            if (tokens[i].IsSymbol("("))
            {
                depth++;
            }
            else if (tokens[i].IsSymbol(")"))
            {
                depth = Math.Max(0, depth - 1);
            }
            else if (depth == 0 && !tokens[i].IsWord("WHEN") && IsBodyStart(tokens, i))
            {
                return tokens[i].Start;
            }
        }

        return null;
    }

    /// <summary>WHEN (condition) or the body: a PL/SQL block (optionally labelled), a compound trigger or a CALL.</summary>
    private static bool IsBodyStart(List<SqlToken> tokens, int i) =>
        tokens[i].IsWord("WHEN") || tokens[i].IsWord("DECLARE") || tokens[i].IsWord("BEGIN") || tokens[i].IsWord("COMPOUND")
        || tokens[i].IsWord("CALL") || (tokens[i].IsSymbol("<") && i + 1 < tokens.Count && tokens[i + 1].IsSymbol("<"));
}
