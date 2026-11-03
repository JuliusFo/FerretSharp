using System.Text.RegularExpressions;

namespace FerretSharp.Core.Query;

/// <summary>A statement of a script: where it is and its text without the terminating <c>;</c>.</summary>
public sealed record SqlStatement(int Start, int Length, string Text)
{
    public int End => Start + Length;
}

/// <summary>
/// The statements of a script (WP-17, part of <see cref="SqlScript"/> since R4b): separated by <c>;</c> or a line with only
/// <c>/</c>, the statement at the cursor, the statements of a selection.
/// </summary>
public static class SqlSplitter
{
    private static readonly Regex BlankLine = new(@"\n[ \t\r\f\v]*\n", RegexOptions.CultureInvariant);

    /// <summary>Words after a column named <c>begin</c> or <c>declare</c> that never start a PL/SQL statement.</summary>
    private static readonly HashSet<string> ContinuesSql = new(StringComparer.OrdinalIgnoreCase)
    {
        "FROM", "AS", "AND", "OR", "IS", "IN", "NOT", "BETWEEN", "LIKE", "WHERE", "GROUP", "ORDER", "HAVING", "UNION", "MINUS",
        "INTERSECT", "EXCEPT", "ASC", "DESC", "NULLS", "THEN", "ELSE", "END", "WHEN", "OVER", "KEEP", "CONNECT", "START", "INTO",
    };

    /// <summary>
    /// The statements of a script, separated by <c>;</c> or a line with only <c>/</c> (SQL*Plus). A blank line separates
    /// nothing (decision of the user, ADR 0014: a view laid out with blank lines was cut apart); a statement without
    /// <c>;</c> runs on to the next one. Comments directly above a statement belong to it; parts with only comments are no
    /// statement. A block – a trigger or another PL/SQL object, <c>BEGIN</c>/<c>DECLARE</c> – holds <c>;</c> and ends only
    /// at a <c>/</c> line or at the end of the text (WP-26), its text with the <c>;</c> after its <c>END</c>.
    /// </summary>
    /// <remarks>
    /// A block behind a statement without <c>;</c> must not fall apart at its inner <c>;</c> – each piece of its body would
    /// run as a statement of its own (review of <c>fix-sql-split-at-semicolon</c>, decision of the user): the
    /// <c>CREATE</c> of a PL/SQL object starts a statement of its own (<c>CREATE</c> is reserved, it never continues one);
    /// a <c>BEGIN</c>/<c>DECLARE</c> block at the start of a line keeps the statement before it up to the <c>/</c> line, as
    /// one statement that <see cref="SqlStatementInfo.RunsIntoBlock"/> refuses.
    /// </remarks>
    public static IReadOnlyList<SqlStatement> Split(string text)
    {
        var statements = new List<SqlStatement>();
        var tokens = SqlTokenizer.Tokenize(text);
        int? start = null;
        var first = 0;
        var end = 0;
        var codeEnd = 0;
        int? commentsAbove = null;
        var hasCode = false;
        bool? block = null;

        void Close()
        {
            if (start is { } s && hasCode)
            {
                statements.Add(new SqlStatement(s, end - s, text[s..end]));
            }

            start = null;
            hasCode = false;
            block = null;
            commentsAbove = null;
        }

        // Decided at the first ";": by then a block's header has been read (its body is the first thing to hold a ";"). Cut
        // at its first ";", a trigger would reach Oracle as a broken one and stop all DML on its table.
        bool InBlock(int i) => hasCode && (block ??= IsBlock(tokens, first, i));

        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (token.Kind == SqlTokenKind.Space)
            {
                continue;
            }

            if (token.IsSymbol("/") && AloneOnLine(text, token))
            {
                Close();
                continue;
            }

            if (token.IsSymbol(";"))
            {
                if (InBlock(i))
                {
                    end = codeEnd = token.End;
                    commentsAbove = null;
                    continue;
                }

                Close();
                continue;
            }

            if (hasCode && CreatesBlock(tokens, i) && !InBlock(i))
            {
                // The comment lines directly above it go with it.
                end = codeEnd;
                var from = commentsAbove ?? i;
                Close();
                (start, first) = (tokens[from].Start, from);
            }

            if (start is null)
            {
                (start, first) = (token.Start, i);
            }

            end = token.End;
            if (token.Kind == SqlTokenKind.Comment)
            {
                commentsAbove ??= hasCode && StartsLine(tokens, i) ? i : null;
            }
            else
            {
                (codeEnd, commentsAbove, hasCode) = (token.End, null, true);
            }
        }

        Close();
        return statements;
    }

    private static bool IsBlock(IReadOnlyList<SqlToken> tokens, int from, int to)
    {
        var significant = SignificantIn(tokens, from, to);
        return significant.Count > 0 && (IsBlock(SqlClassifier.KindOf(significant)) || HasBlockStart(tokens, from, to));
    }

    /// <summary>
    /// <c>BEGIN</c> or <c>DECLARE</c> at the start of a line after the statement's first word, followed by a word that
    /// does not continue SQL (<see cref="ContinuesSql"/>) or a label (<c>&lt;&lt;</c>): the block behind a statement
    /// without <c>;</c>. A column named <c>begin</c> at the start of a line followed by an alias looks the same – then the
    /// statement is refused, never cut.
    /// </summary>
    private static bool HasBlockStart(IReadOnlyList<SqlToken> tokens, int from, int to)
    {
        for (var i = from + 1; i < to; i++)
        {
            if ((tokens[i].IsWord("BEGIN") || tokens[i].IsWord("DECLARE")) && StartsLine(tokens, i)
                && SignificantIn(tokens, i + 1, to) is [var next, ..]
                && ((next.Kind == SqlTokenKind.Word && !ContinuesSql.Contains(next.Value)) || next.IsSymbol("<<")))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The <c>CREATE</c> of a trigger or another PL/SQL object (<c>CREATE [OR REPLACE] [EDITIONABLE] PACKAGE …</c>).</summary>
    private static bool CreatesBlock(IReadOnlyList<SqlToken> tokens, int i) =>
        tokens[i].IsWord("CREATE") && IsBlock(SqlClassifier.KindOf(SignificantIn(tokens, i, Math.Min(tokens.Count, i + 32))));

    private static bool StartsLine(IReadOnlyList<SqlToken> tokens, int i) =>
        i == 0 || (tokens[i - 1].Kind == SqlTokenKind.Space && tokens[i - 1].Value.Contains('\n', StringComparison.Ordinal));

    private static List<SqlToken> SignificantIn(IReadOnlyList<SqlToken> tokens, int from, int to)
    {
        var significant = new List<SqlToken>();
        for (var i = from; i < to; i++)
        {
            if (tokens[i].Kind is not (SqlTokenKind.Space or SqlTokenKind.Comment))
            {
                significant.Add(tokens[i]);
            }
        }

        return significant;
    }

    private static bool IsBlock(SqlStatementKind kind) =>
        kind is SqlStatementKind.Trigger or SqlStatementKind.SystemTrigger or SqlStatementKind.PlSqlObject or SqlStatementKind.PlSql;

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

    /// <summary>
    /// The text with its last statement ended, so that a statement appended after it stays one of its own: a <c>;</c>
    /// right after the statement's last code (before a comment that follows it), a <c>/</c> line after a block. Unchanged
    /// if it already ends at a <c>;</c> or a <c>/</c> line, or holds no statement.
    /// </summary>
    public static string Terminated(string text)
    {
        if (Split(text) is not [.., var last])
        {
            return text;
        }

        var rest = SqlTokenizer.Tokenize(text[last.End..]);
        if (rest.Any(t => t.IsSymbol(";") || (t.IsSymbol("/") && AloneOnLine(text, t with { Start = t.Start + last.End }))))
        {
            return text;
        }

        // A statement that runs into a block holds its ";" and ends only at a "/" line, as a block does.
        var code = SqlTokenizer.Significant(last.Text);
        if (IsBlock(SqlClassifier.KindOf(code)) || code.Any(t => t.IsSymbol(";")))
        {
            return text[..last.End] + "\n/" + text[last.End..];
        }

        var end = last.Start + code[^1].End;
        return text[..end] + ";" + text[end..];
    }

    /// <summary>
    /// The script with <paramref name="statement"/> appended after a blank line, both ended (<see cref="Terminated"/>) –
    /// what the SQL history adds; <c>Start</c> is where the statement begins.
    /// </summary>
    public static (string Text, int Start) Append(string script, string statement)
    {
        var before = Terminated(script.TrimEnd());
        var prefix = before.Length == 0 ? "" : before + "\n\n";
        return (prefix + Terminated(statement.Trim()) + "\n", prefix.Length);
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

    private static bool AloneOnLine(string text, SqlToken token)
    {
        var lineStart = token.Start == 0 ? 0 : text.LastIndexOf('\n', token.Start - 1) + 1;
        var lineEnd = text.IndexOf('\n', token.End);
        lineEnd = lineEnd < 0 ? text.Length : lineEnd;
        return text[lineStart..token.Start].Trim().Length == 0 && text[token.End..lineEnd].Trim().Length == 0;
    }
}
