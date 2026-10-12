using System.Text;
using FerretSharp.Core.Resources;
using FerretSharp.Core.Schema;

namespace FerretSharp.Core.Audit;

/// <summary>The placeholders of an audit template (<c>{table}</c> …).</summary>
internal enum AuditPlaceholder
{
    Table,
    AuditTable,
    Trigger,
    Owner,
    Column,
    Type,

    /// <summary>The column's dictionary name for a string literal (<c>'</c> doubled), e.g. <c>UPDATING('{name}')</c>.</summary>
    Name,
}

/// <summary>Which base columns a loop repeats over.</summary>
internal enum AuditLoopFilter
{
    All,

    /// <summary>Only columns that can be compared with <c>&lt;&gt;</c> (<see cref="OracleTypes.IsComparable"/>).</summary>
    Comparable,

    /// <summary>Only the others (LOBs, XMLTYPE …): their change shows through <c>UPDATING('{name}')</c>.</summary>
    Incomparable,
}

/// <summary>A piece of a parsed pattern: text, a placeholder or a loop over the base columns.</summary>
internal abstract record PatternPart;

internal sealed record LiteralPart(string Text) : PatternPart;

internal sealed record PlaceholderPart(AuditPlaceholder Placeholder) : PatternPart;

/// <param name="Line">Line of the <c>{each}</c> in the template, for warnings.</param>
/// <param name="Filter">Which base columns it repeats over.</param>
/// <param name="Separator">Text between two repetitions; null repeats the body only.</param>
internal sealed record LoopPart(int Line, AuditLoopFilter Filter, string? Separator, IReadOnlyList<PatternPart> Body) : PatternPart;

/// <summary>
/// The minimal template syntax of WP-26: <c>{table}</c> placeholders, <c>{each [comparable | incomparable] [sep="…"]}…{end}</c> loops over
/// the base columns (not nested), <c>{{</c> and <c>}}</c> for literal braces. Parsing collects errors with line numbers;
/// rendering asks the caller for the values.
/// </summary>
internal static class AuditPattern
{
    private static readonly Dictionary<string, AuditPlaceholder> Names = new(StringComparer.Ordinal)
    {
        ["table"] = AuditPlaceholder.Table,
        ["audit_table"] = AuditPlaceholder.AuditTable,
        ["trigger"] = AuditPlaceholder.Trigger,
        ["owner"] = AuditPlaceholder.Owner,
        ["column"] = AuditPlaceholder.Column,
        ["type"] = AuditPlaceholder.Type,
        ["name"] = AuditPlaceholder.Name,
    };

    /// <summary>The placeholder as written in a template, for messages.</summary>
    public static string Written(string name) => "{" + name + "}";

    /// <param name="text">The pattern; line breaks as <c>\n</c>.</param>
    /// <param name="firstLine">Template line of the pattern's first character.</param>
    /// <param name="directive">The directive the pattern stands in (<c>audit-table</c> …); null for the trigger source,
    /// which also takes loops and <c>{column}</c>/<c>{type}</c> inside them.</param>
    /// <param name="allowed">The placeholders outside loops.</param>
    public static IReadOnlyList<PatternPart> Parse(
        string text, int firstLine, string? directive, IReadOnlySet<AuditPlaceholder> allowed, List<AuditTemplateError> errors)
    {
        var top = new List<PatternPart>();
        List<PatternPart>? body = null;
        var loopLine = 0;
        var filter = AuditLoopFilter.All;
        string? separator = null;
        var literal = new StringBuilder();

        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (c is '{' or '}' && At(text, i + 1) == c)
            {
                literal.Append(c);
                i += 2;
                continue;
            }

            if (c == '}')
            {
                Error(i, AuditText.BraceNotOpened);
                i++;
                continue;
            }

            if (c != '{')
            {
                literal.Append(c);
                i++;
                continue;
            }

            var (end, quoteOpen) = TagEnd(text, i);
            if (end < 0)
            {
                Error(i, quoteOpen ? AuditText.SeparatorNotClosed : AuditText.BraceNotClosed);
                break;
            }

            var line = LineAt(i);
            var tag = text[(i + 1)..end].Trim();
            i = end + 1;
            var space = tag.IndexOfAny([' ', '\t']);
            var (word, options) = space < 0 ? (tag, "") : (tag[..space], tag[(space + 1)..].Trim());
            switch (word)
            {
                case "each" when directive is not null:
                case "end" when directive is not null:
                    errors.Add(new(line, TextFormat.Format(AuditText.PlaceholderNotInDirective, Written(word), directive)));
                    break;
                case "each" when body is not null:
                    errors.Add(new(line, TextFormat.Format(AuditText.LoopNested, Written("end"), Written("each"))));
                    break;
                case "each":
                    Flush();
                    (filter, separator) = LoopOptions(options, line, errors);
                    body = [];
                    loopLine = line;
                    break;
                case "end" when body is null:
                    errors.Add(new(line, TextFormat.Format(AuditText.LoopUnpaired, Written("end"), Written("each"))));
                    break;
                case "end":
                    NoOptions();
                    Flush();
                    top.Add(new LoopPart(loopLine, filter, separator, body));
                    body = null;
                    break;
                default:
                    if (!Names.TryGetValue(word, out var placeholder))
                    {
                        errors.Add(new(line, TextFormat.Format(AuditText.PlaceholderUnknown, Written(tag))));
                    }
                    else if (InLoop(placeholder) ? directive is not null : !allowed.Contains(placeholder))
                    {
                        errors.Add(new(line, TextFormat.Format(AuditText.PlaceholderNotInDirective, Written(word), directive)));
                    }
                    else if (InLoop(placeholder) && body is null)
                    {
                        errors.Add(new(line, TextFormat.Format(AuditText.PlaceholderOutsideLoop, Written(word), Written("each"), Written("end"))));
                    }
                    else if (NoOptions())
                    {
                        Flush();
                        (body ?? top).Add(new PlaceholderPart(placeholder));
                    }

                    break;
            }

            bool NoOptions()
            {
                if (options.Length == 0)
                {
                    return true;
                }

                errors.Add(new(line, TextFormat.Format(AuditText.PlaceholderTakesNoOptions, Written(word))));
                return false;
            }
        }

        Flush();
        if (body is not null)
        {
            errors.Add(new(loopLine, TextFormat.Format(AuditText.LoopUnpaired, Written("each"), Written("end"))));
        }

        return top;

        void Flush()
        {
            if (literal.Length > 0)
            {
                (body ?? top).Add(new LiteralPart(literal.ToString()));
                literal.Clear();
            }
        }

        int LineAt(int at)
        {
            var line = firstLine;
            for (var k = 0; k < at; k++)
            {
                if (text[k] == '\n')
                {
                    line++;
                }
            }

            return line;
        }

        void Error(int at, string message) => errors.Add(new(LineAt(at), TextFormat.Format(message)));
    }

    /// <summary>
    /// Renders a pattern. <paramref name="value"/> answers a placeholder, with the column inside a loop;
    /// <paramref name="columnsOf"/> gives the columns a loop repeats over.
    /// </summary>
    public static string Render(
        IReadOnlyList<PatternPart> parts, Func<AuditPlaceholder, ColumnInfo?, string> value, Func<LoopPart, IReadOnlyList<ColumnInfo>> columnsOf)
    {
        var text = new StringBuilder();
        foreach (var part in parts)
        {
            switch (part)
            {
                case LiteralPart literal:
                    text.Append(literal.Text);
                    break;
                case PlaceholderPart placeholder:
                    text.Append(value(placeholder.Placeholder, null));
                    break;
                case LoopPart loop:
                    var first = true;
                    foreach (var column in columnsOf(loop))
                    {
                        if (!first && loop.Separator is not null)
                        {
                            text.Append(loop.Separator);
                        }

                        first = false;
                        foreach (var inner in loop.Body)
                        {
                            text.Append(inner switch
                            {
                                LiteralPart l => l.Text,
                                PlaceholderPart p => value(p.Placeholder, column),
                                _ => throw new InvalidOperationException("Loops do not nest."),
                            });
                        }
                    }

                    break;
            }
        }

        return text.ToString();
    }

    /// <summary>A placeholder of the column a loop repeats over.</summary>
    private static bool InLoop(AuditPlaceholder placeholder) => placeholder is AuditPlaceholder.Column or AuditPlaceholder.Type or AuditPlaceholder.Name;

    /// <summary>The loops of a pattern.</summary>
    public static IEnumerable<LoopPart> Loops(IReadOnlyList<PatternPart> parts) => parts.OfType<LoopPart>();

    private static char At(string text, int i) => i < text.Length ? text[i] : '\0';

    /// <summary>The closing brace of the tag at <paramref name="start"/>, or -1 if the line ends first (inside a quote: <c>QuoteOpen</c>).</summary>
    private static (int End, bool QuoteOpen) TagEnd(string text, int start)
    {
        var quoted = false;
        for (var i = start + 1; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\n')
            {
                return (-1, quoted);
            }

            if (quoted && c == '\\')
            {
                i++;
            }
            else if (c == '"')
            {
                quoted = !quoted;
            }
            else if (!quoted && c == '}')
            {
                return (i, false);
            }
            else if (!quoted && c == '{')
            {
                return (-1, false);
            }
        }

        return (-1, quoted);
    }

    /// <summary><c>comparable</c> or <c>incomparable</c>, and <c>sep="…"</c> with the escapes <c>\n \t \" \\</c>.</summary>
    private static (AuditLoopFilter Filter, string? Separator) LoopOptions(string options, int line, List<AuditTemplateError> errors)
    {
        var filter = AuditLoopFilter.All;
        string? separator = null;
        var i = 0;
        while (i < options.Length)
        {
            if (char.IsWhiteSpace(options[i]))
            {
                i++;
                continue;
            }

            if (options.AsSpan(i).StartsWith("sep=\"", StringComparison.Ordinal))
            {
                i += 5;
                var value = new StringBuilder();
                while (i < options.Length && options[i] != '"')
                {
                    if (options[i] == '\\')
                    {
                        var escape = At(options, i + 1);
                        var decoded = escape switch { 'n' => '\n', 't' => '\t', '"' => '"', '\\' => '\\', _ => (char?)null };
                        if (decoded is null)
                        {
                            errors.Add(new(line, TextFormat.Format(AuditText.SeparatorEscape, escape)));
                        }
                        else
                        {
                            value.Append(decoded.Value);
                        }

                        i += 2;
                    }
                    else
                    {
                        value.Append(options[i++]);
                    }
                }

                i++; // the closing quote (TagEnd found it)
                if (separator is not null)
                {
                    errors.Add(new(line, TextFormat.Format(AuditText.LoopOptionRepeated, "sep")));
                }

                separator = value.ToString();
                continue;
            }

            var end = i;
            while (end < options.Length && !char.IsWhiteSpace(options[end]))
            {
                end++;
            }

            var word = options[i..end];
            if (word is "comparable" or "incomparable")
            {
                if (filter != AuditLoopFilter.All)
                {
                    var same = (filter == AuditLoopFilter.Comparable) == (word == "comparable");
                    errors.Add(new(line, same ? TextFormat.Format(AuditText.LoopOptionRepeated, word) : AuditText.LoopFilterTwice));
                }

                filter = word == "comparable" ? AuditLoopFilter.Comparable : AuditLoopFilter.Incomparable;
            }
            else
            {
                errors.Add(new(line, TextFormat.Format(AuditText.LoopOptionUnknown, word)));
            }

            i = end;
        }

        return (filter, separator);
    }
}
