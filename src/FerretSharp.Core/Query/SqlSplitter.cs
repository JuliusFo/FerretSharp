using System.Text.RegularExpressions;

namespace FerretSharp.Core.Query;

/// <summary>A statement of a script: where it is and its text without the terminating <c>;</c>.</summary>
public sealed record SqlStatement(int Start, int Length, string Text)
{
    public int End => Start + Length;
}

/// <summary>
/// The statements of a script (WP-17, part of <see cref="SqlScript"/> since R4b): separated by <c>;</c>, a blank line or a
/// line with only <c>/</c>, the statement at the cursor, the statements of a selection.
/// </summary>
public static class SqlSplitter
{
    private static readonly Regex BlankLine = new(@"\n[ \t\r\f\v]*\n", RegexOptions.CultureInvariant);

    /// <summary>
    /// The statements of a script, separated by <c>;</c>, a blank line or a line with only <c>/</c> (SQL*Plus). Comments
    /// directly above a statement belong to it; parts with only comments are no statement.
    /// </summary>
    public static IReadOnlyList<SqlStatement> Split(string text)
    {
        var statements = new List<SqlStatement>();
        var tokens = SqlTokenizer.Tokenize(text);
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

    private static bool AloneOnLine(string text, SqlToken token)
    {
        var lineStart = token.Start == 0 ? 0 : text.LastIndexOf('\n', token.Start - 1) + 1;
        var lineEnd = text.IndexOf('\n', token.End);
        lineEnd = lineEnd < 0 ? text.Length : lineEnd;
        return text[lineStart..token.Start].Trim().Length == 0 && text[token.End..lineEnd].Trim().Length == 0;
    }
}
