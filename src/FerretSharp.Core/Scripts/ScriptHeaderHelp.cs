using System.Text.RegularExpressions;
using FerretSharp.Core.Connections;
using FerretSharp.Core.Query;
using FerretSharp.Core.Resources;
using FerretSharp.Core.Schema;

namespace FerretSharp.Core.Scripts;

public enum ScriptMarkerSeverity
{
    Warning,
    Info,
}

/// <summary>A range of a SQL tab's text the editor marks (WP-89), by UTF-16 offsets into the text it was made for.</summary>
public sealed record ScriptHeaderMarker(int Start, int Length, ScriptMarkerSeverity Severity, string Message);

/// <summary>What the hover over a header line shows (WP-89): Markdown paragraphs about the range given.</summary>
public sealed record ScriptHeaderHover(int Start, int Length, IReadOnlyList<string> Paragraphs);

/// <summary>
/// Help with a script's header where it is typed, the SQL editor (WP-89, ADR 0051): marks the header lines that cannot be
/// used while typing (the same rules saving applies, <see cref="ScriptHeaders.ForSave"/>), explains a header line on hover
/// – a <c>@target</c> with what the connection's schema says about it – and completes <c>-- @…</c>, the types of a
/// <c>@param</c> and the binds, tables and columns of a <c>@target</c>. Everything is decided here; the editor only shows it.
/// </summary>
public static partial class ScriptHeaderHelp
{
    /// <summary>The directives FerretSharp reads, as written.</summary>
    private static readonly string[] Known = ["description", "param", "target"];

    /// <summary>How many columns the hover over a <c>@target</c> names before "and N more".</summary>
    internal const int ShownColumns = 8;

    /// <summary>
    /// The marks for <paramref name="text"/> that need no schema: header lines saving reports (a <c>@param</c> or
    /// <c>@target</c> without its bind, a syntax error, a target twice), unknown directives (a typo close to a known one is a
    /// warning), a <c>-- @param</c>, <c>@target</c> or <c>@description</c> below the header (it has no effect there). The
    /// schema's marks come apart (<see cref="TargetMarkersAsync"/>): they may wait for the dictionary, these never do
    /// (review WP-89, 4).
    /// </summary>
    public static IReadOnlyList<ScriptHeaderMarker> Markers(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var (spans, lines) = Lines(text);
        var read = ScriptHeaders.ReadLines(lines);
        var markers = new List<ScriptHeaderMarker>();
        if (read.DirectivesEnd > 0)
        {
            foreach (var error in ScriptHeaders.ForSave(text, [], null, fileHeaderInText: true).Errors)
            {
                markers.Add(Mark(text, spans[error.Line - 1], ScriptMarkerSeverity.Warning, error.Message));
            }

            for (var i = read.First; i < read.DirectivesEnd; i++)
            {
                var name = ScriptHeaders.Directive().Match(lines[i]).Groups["name"].Value;
                if (!IsKnown(name))
                {
                    markers.Add(Similar(name) is { } meant
                        ? Mark(text, spans[i], ScriptMarkerSeverity.Warning, TextFormat.Format(ScriptText.Marker_UnknownSimilar, name, meant))
                        : Mark(text, spans[i], ScriptMarkerSeverity.Info, TextFormat.Format(ScriptText.Marker_Unknown, name)));
                }
            }
        }

        for (var i = read.DirectivesEnd; i < lines.Length; i++)
        {
            if (ScriptHeaders.Directive().Match(lines[i]) is { Success: true } directive && IsKnown(directive.Groups["name"].Value))
            {
                markers.Add(Mark(text, spans[i], ScriptMarkerSeverity.Warning, ScriptText.Marker_NotHeader));
            }
        }

        return markers;
    }

    /// <summary>
    /// The marks of the targets in <paramref name="text"/>'s header that the connection's schema contradicts: a table it does
    /// not have (a hint – the script may be for another product), a column its table lacks (a warning). Reads table
    /// structures through the schema cache; a dictionary error leaves the marks out.
    /// </summary>
    public static async Task<IReadOnlyList<ScriptHeaderMarker>> TargetMarkersAsync(string text, SchemaCache schema, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(schema);
        var (spans, lines) = Lines(text);
        var markers = new List<ScriptHeaderMarker>();
        foreach (var (target, line) in ScriptHeaders.ReadLines(lines).Targets)
        {
            if (await TargetProblemAsync(target, schema, cancellationToken) is { } problem)
            {
                markers.Add(Mark(text, spans[line - 1], problem.Severity, problem.Message));
            }
        }

        return markers;
    }

    /// <summary>
    /// The hover at <paramref name="offset"/>: on a header line what it does (on a <c>@target</c> also whether the
    /// connection has its column and on which columns the script shows); on a known directive below the header why it has
    /// no effect; elsewhere null.
    /// </summary>
    public static async Task<ScriptHeaderHover?> HoverAsync(string text, int offset, SchemaCache? schema, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);
        // Every hover anywhere in the SQL asks: the line under the mouse first, the whole text only for a header line (review WP-89, 6).
        if (!ScriptHeaders.Directive().IsMatch(LineAround(text, offset)))
        {
            return null;
        }

        var (spans, lines) = Lines(text);
        var index = LineAt(spans, offset);
        if (index < 0 || ScriptHeaders.Directive().Match(lines[index]) is not { Success: true } directive)
        {
            return null;
        }

        var read = ScriptHeaders.ReadLines(lines);
        var name = directive.Groups["name"].Value;
        var span = spans[index];
        if (index < read.First || index >= read.DirectivesEnd)
        {
            return IsKnown(name) ? new ScriptHeaderHover(span.Start, span.Length, [ScriptText.Hover_NotHeader]) : null;
        }

        var paragraphs = new List<string>();
        switch (name.ToLowerInvariant())
        {
            case "description":
                paragraphs.Add(ScriptText.Hover_Description);
                break;
            case "param":
                paragraphs.Add(TextFormat.Format(ScriptText.Hover_Param, string.Join(", ", ScriptHeaders.TypeNamesWritten.Select(Code))));
                break;
            case "target":
                paragraphs.Add(ScriptText.Hover_Target);
                if (schema is not null && read.Targets.FirstOrDefault(t => t.Line == index + 1).Target is { } target)
                {
                    paragraphs.Add(await TargetStatusAsync(target, schema, cancellationToken));
                }

                break;
            default:
                paragraphs.Add(Similar(name) is { } meant
                    ? TextFormat.Format(ScriptText.Hover_UnknownSimilar, Code("@" + name), Code("@" + meant))
                    : TextFormat.Format(ScriptText.Hover_Unknown, Code("@" + name)));
                break;
        }

        paragraphs.Add(ScriptText.Hover_More);
        return new ScriptHeaderHover(span.Start, span.Length, paragraphs);
    }

    /// <summary>
    /// The completion items at <paramref name="offset"/> on a header line being typed (<c>-- @</c> at the start of a line
    /// that only header lines stand above): the directives after <c>@</c>; after <c>@param name</c> the types, then
    /// <c>list</c>; after <c>@param</c> or <c>@target</c> the binds of the SQL; after <c>@target name</c> the tables and
    /// views (without owner), after <c>TABLE.</c> its columns. Null on any other line – the SQL completion then answers.
    /// </summary>
    public static async Task<IReadOnlyList<SqlCompletionItem>?> CompleteAsync(string text, int offset, SchemaCache? schema, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);
        offset = Math.Clamp(offset, 0, text.Length);
        // Asked on every completion in the SQL: the line at the cursor first (review WP-89, 6).
        if (!HeaderStart().IsMatch(LineAround(text, offset, toOffset: true)))
        {
            return null;
        }

        var (spans, lines) = Lines(text);
        var index = LineAt(spans, offset);
        if (index < 0 || !OnlyHeaderAbove(lines, index))
        {
            return null;
        }

        var typed = text[spans[index].Start..offset];
        if (HeaderStart().Match(typed) is not { Success: true } start)
        {
            return null;
        }

        var rest = start.Groups["rest"].Value;
        if (DirectiveTyped().IsMatch(rest))
        {
            return
            [
                Item("description", "description ", SqlCompletionKind.Keyword, ScriptText.Completion_Description),
                Item("param", "param ", SqlCompletionKind.Keyword, ScriptText.Completion_Param),
                Item("target", "target ", SqlCompletionKind.Keyword, ScriptText.Completion_Target),
            ];
        }

        if (ParamTypeTyped().IsMatch(rest))
        {
            return [.. ScriptHeaders.TypeNamesWritten.Select(t => Item(t, t, SqlCompletionKind.Keyword, ScriptText.Completion_Type))];
        }

        if (ParamListTyped().IsMatch(rest))
        {
            return [Item("list", "list", SqlCompletionKind.Keyword, ScriptText.Completion_List)];
        }

        if (BindTyped().Match(rest) is { Success: true } bind)
        {
            var parsed = ScriptHeaders.Parse(text);
            var binds = ScriptHeaders.ParametersOf(parsed.Body, []).Select(p => p.Name);
            if (bind.Groups["directive"].Value.Equals("param", StringComparison.OrdinalIgnoreCase))
            {
                binds = binds.Where(b => !parsed.Header.Parameters.Any(p => p.Name.Equals(b, StringComparison.OrdinalIgnoreCase)));
            }

            return [.. binds.Select(b => Item(b, ParameterName(b), SqlCompletionKind.Variable, ScriptText.Completion_Bind))];
        }

        if (schema is null)
        {
            return [];
        }

        if (TableTyped().IsMatch(rest))
        {
            return
            [
                .. schema.Tables.Where(t => t.Synonym is not null || t.Owner == schema.Owner)
                    .Select(t => SqlCompletion.TableItem(t, SqlCompletion.Quote(t.DisplayName), t.DisplayName, _ => null)),
            ];
        }

        if (ColumnTyped().Match(rest) is { Success: true } column
            && ScriptTargets.Find(Oracle.OracleIdentifier.Normalize(column.Groups["table"].Value), schema.Tables, schema.Owner) is { } table)
        {
            try
            {
                var details = await schema.GetDetailsAsync(table, cancellationToken);
                return [.. details.Columns.Select(c => Item(c.Name, SqlCompletion.Quote(c.Name), SqlCompletionKind.Column, c.DisplayType))];
            }
            catch (DatabaseException)
            {
                return [];
            }
        }

        return [];
    }

    /// <summary>
    /// <paramref name="statement"/> without the header lines at its start (WP-89, decision of the user 2026-11-13): the first
    /// statement of a script tab carries them as a comment – the line above the result and the history show the SQL alone.
    /// What runs keeps them (offsets of marks and errors stay right).
    /// </summary>
    public static string WithoutHeader(string statement)
    {
        ArgumentNullException.ThrowIfNull(statement);
        if (!statement.AsSpan().TrimStart().StartsWith("--", StringComparison.Ordinal))
        {
            return statement; // asked on every render (result header, chips): most statements have no header (review WP-89, 6)
        }

        var lines = statement.ReplaceLineEndings("\n").Split('\n');
        var read = ScriptHeaders.ReadLines(lines);
        if (read.DirectivesEnd == 0)
        {
            return statement;
        }

        var rest = string.Join('\n', lines[read.DirectivesEnd..]).TrimStart();
        return rest.Length == 0 ? statement : rest;
    }

    private static async Task<(ScriptMarkerSeverity Severity, string Message)?> TargetProblemAsync(
        ScriptTarget target, SchemaCache schema, CancellationToken cancellationToken)
    {
        if (ScriptTargets.Find(target.Table, schema.Tables, schema.Owner) is not { } table)
        {
            // Not a mistake for sure: the script may be for another product's schema (WP-84).
            return (ScriptMarkerSeverity.Info, TextFormat.Format(ScriptText.Target_TableMissing, target.Table));
        }

        try
        {
            var details = await schema.GetDetailsAsync(table, cancellationToken);
            return details.Columns.Any(c => c.Name == target.Column)
                ? null
                : (ScriptMarkerSeverity.Warning, TextFormat.Format(ScriptText.Target_ColumnMissing, table.DisplayName, target.Column));
        }
        catch (DatabaseException)
        {
            return null;
        }
    }

    /// <summary>What the schema says about a target, as Markdown: found (and on which columns the script shows) or not.</summary>
    private static async Task<string> TargetStatusAsync(ScriptTarget target, SchemaCache schema, CancellationToken cancellationToken)
    {
        if (ScriptTargets.Find(target.Table, schema.Tables, schema.Owner) is not { } table)
        {
            return TextFormat.Format(ScriptText.Target_TableMissing, Code(target.Table));
        }

        TableDetails details;
        try
        {
            details = await schema.GetDetailsAsync(table, cancellationToken);
        }
        catch (DatabaseException ex)
        {
            return TextFormat.Format(ScriptText.Hover_TargetUnchecked, Code(ex.Message));
        }

        if (details.Columns.FirstOrDefault(c => c.Name == target.Column) is not { } column)
        {
            return TextFormat.Format(ScriptText.Target_ColumnMissing, Code(table.DisplayName), Code(target.Column));
        }

        var reached = ScriptTargets.ReachedBy(new ColumnRef(table.Ref, column.Name), schema.Tables.Select(t => t.Ref), schema.LinksOf);
        // The type too is the dictionary's text (an object type's quoted name): only in a code span (review WP-89, 2).
        var found = TextFormat.Format(ScriptText.Hover_TargetFound, Code(table.DisplayName + "." + column.Name), Code(column.DisplayType));
        if (reached.Count == 1)
        {
            return found + " " + ScriptText.Hover_TargetAlone;
        }

        var others = reached.Skip(1).Select(c => Code(Label(c, schema.Owner))).ToList();
        var list = string.Join(", ", others.Take(ShownColumns))
            + (others.Count > ShownColumns ? " " + TextFormat.Format(ScriptText.Hover_TargetAndMore, others.Count - ShownColumns) : "");
        return found + " " + TextFormat.Plural(others.Count, ScriptText.Hover_TargetShowsOnOne, ScriptText.Hover_TargetShowsOnOther, list);
    }

    private static string Label(ColumnRef column, string owner) =>
        column.Table.Owner == owner ? column.Table.Name + "." + column.Column : column.ToString();

    /// <summary>Text as a Markdown code span: shown as it is, whatever it holds (a quoted name may hold any character).</summary>
    internal static string Code(string text) => "`" + text.ReplaceLineEndings(" ").Replace('`', '\'') + "`";

    private static bool IsKnown(string name) => Known.Contains(name, StringComparer.OrdinalIgnoreCase);

    /// <summary>The known directive a typo is close to (at most two edits, <c>@tagret</c>, <c>@parm</c>); null for none.</summary>
    /// <remarks>
    /// Only names within two characters of a known one's length are compared: the name comes from shared files and has no
    /// limit, the distance's table grows with it (review WP-89, 5).
    /// </remarks>
    internal static string? Similar(string name)
    {
        if (name.Length < 3)
        {
            return null;
        }

        var lower = name.ToLowerInvariant();
        return Known.Where(k => Math.Abs(k.Length - lower.Length) <= 2)
            .Select(k => (Known: k, Distance: Distance(lower, k)))
            .Where(k => k.Distance <= 2)
            .OrderBy(k => k.Distance)
            .Select(k => k.Known)
            .FirstOrDefault();
    }

    /// <summary>Edit distance with swapped neighbors as one edit (optimal string alignment).</summary>
    private static int Distance(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++)
        {
            d[i, 0] = i;
        }

        for (var j = 0; j <= b.Length; j++)
        {
            d[0, j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                {
                    d[i, j] = Math.Min(d[i, j], d[i - 2, j - 2] + 1);
                }
            }
        }

        return d[a.Length, b.Length];
    }

    private static SqlCompletionItem Item(string label, string insert, SqlCompletionKind kind, string? detail) => new(label, insert, kind, detail, 0);

    /// <summary>A bind as the header writes it: plain, or in quotes when it has characters a plain name cannot have.</summary>
    private static string ParameterName(string name) =>
        name.All(c => char.IsLetterOrDigit(c) || c is '_' or '$' or '#') ? name : "\"" + name.Replace("\"", "", StringComparison.Ordinal) + "\"";

    /// <summary>Every line before <paramref name="index"/> is blank (only before the first) or a header line: the header goes on there.</summary>
    private static bool OnlyHeaderAbove(string[] lines, int index)
    {
        var i = 0;
        while (i < index && lines[i].Trim().Length == 0)
        {
            i++;
        }

        for (; i < index; i++)
        {
            if (!ScriptHeaders.Directive().IsMatch(lines[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static ScriptHeaderMarker Mark(string text, (int Start, int Length) span, ScriptMarkerSeverity severity, string message)
    {
        var start = span.Start;
        var end = span.Start + span.Length;
        while (start < end && char.IsWhiteSpace(text[start]))
        {
            start++;
        }

        while (end > start && char.IsWhiteSpace(text[end - 1]))
        {
            end--;
        }

        return new ScriptHeaderMarker(start, end - start, severity, message);
    }

    /// <summary>
    /// The line holding <paramref name="offset"/>, without splitting the whole text – up to <paramref name="offset"/> with
    /// <paramref name="toOffset"/>. The separators are those of <see cref="Lines"/>.
    /// </summary>
    private static string LineAround(string text, int offset, bool toOffset = false)
    {
        offset = Math.Clamp(offset, 0, text.Length);
        var start = offset;
        while (start > 0 && !IsLineBreak(text[start - 1]))
        {
            start--;
        }

        var end = offset;
        while (!toOffset && end < text.Length && !IsLineBreak(text[end]))
        {
            end++;
        }

        return text[start..end];
    }

    private static bool IsLineBreak(char c) => c is '\r' or '\n' or '\f' or '\u0085' or '\u2028' or '\u2029';

    /// <summary>The line holding <paramref name="offset"/> (its end belongs to it); -1 outside the text.</summary>
    private static int LineAt(IReadOnlyList<(int Start, int Length)> spans, int offset)
    {
        for (var i = 0; i < spans.Count; i++)
        {
            if (offset >= spans[i].Start && offset <= spans[i].Start + spans[i].Length)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// The lines of <paramref name="text"/> with their offsets – split where <see cref="string.ReplaceLineEndings()"/> splits
    /// (CR, LF, CRLF, NEL, LS, FF, PS), so line numbers agree with <see cref="ScriptHeaders"/>.
    /// </summary>
    internal static ((int Start, int Length)[] Spans, string[] Lines) Lines(string text)
    {
        var spans = new List<(int Start, int Length)>();
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (IsLineBreak(text[i]))
            {
                spans.Add((start, i - start));
                if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }

                start = i + 1;
            }
        }

        spans.Add((start, text.Length - start));
        return ([.. spans], [.. spans.Select(s => text.Substring(s.Start, s.Length))]);
    }

    /// <summary>The start of a header line up to the cursor: <c>-- @</c> and what follows it.</summary>
    [GeneratedRegex(@"^\s*--\s*@(?<rest>[^\r\n]*)$")]
    private static partial Regex HeaderStart();

    [GeneratedRegex(@"^[A-Za-z-]*$")]
    private static partial Regex DirectiveTyped();

    [GeneratedRegex(@"^param\s+:?(?:""[^""]+""|[\p{L}\p{N}_$#]+)\s+[A-Za-z]*$", RegexOptions.IgnoreCase)]
    private static partial Regex ParamTypeTyped();

    [GeneratedRegex(@"^param\s+:?(?:""[^""]+""|[\p{L}\p{N}_$#]+)\s+[A-Za-z]+\s+[A-Za-z]*$", RegexOptions.IgnoreCase)]
    private static partial Regex ParamListTyped();

    [GeneratedRegex(@"^(?<directive>param|target)\s+:?[\p{L}\p{N}_$#]*$", RegexOptions.IgnoreCase)]
    private static partial Regex BindTyped();

    [GeneratedRegex(@"^target\s+:?(?:""[^""]+""|[\p{L}\p{N}_$#]+)\s+[\p{L}\p{N}_$#]*$", RegexOptions.IgnoreCase)]
    private static partial Regex TableTyped();

    [GeneratedRegex(@"^target\s+:?(?:""[^""]+""|[\p{L}\p{N}_$#]+)\s+(?<table>""[^""]+""|[\p{L}_$#][\p{L}\p{N}_$#]*)\s*\.\s*[\p{L}\p{N}_$#]*$", RegexOptions.IgnoreCase)]
    private static partial Regex ColumnTyped();
}
