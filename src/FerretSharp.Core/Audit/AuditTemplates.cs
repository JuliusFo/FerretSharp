using FerretSharp.Core.Oracle;
using System.Text;
using System.Text.RegularExpressions;
using FerretSharp.Core.Ddl;
using FerretSharp.Core.Query;
using FerretSharp.Core.Resources;
using FerretSharp.Core.Schema;

namespace FerretSharp.Core.Audit;

/// <summary>
/// Reads audit templates (WP-26): an SQL file steered by directive lines (<c>-- @audit-table</c>, <c>-- @trigger</c>,
/// <c>-- @table-comment</c>, <c>-- @columns</c>, <c>-- @base-columns</c>, <c>-- @trigger-source</c>). Syntax and rules:
/// <c>docs/audit-templates.md</c>.
/// </summary>
public static partial class AuditTemplates
{
    private const string DefaultResource = "FerretSharp.Core.Audit.DefaultAuditTemplate.sql";

    private static readonly Lazy<string> DefaultTextValue = new(() =>
    {
        using var stream = typeof(AuditTemplates).Assembly.GetManifestResourceStream(DefaultResource)
            ?? throw new InvalidOperationException($"Resource {DefaultResource} is missing.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    });

    private static readonly Lazy<AuditTemplate> DefaultValue = new(() => Parse(DefaultText));

    /// <summary>The built-in template (<c>Audit/DefaultAuditTemplate.sql</c>).</summary>
    public static string DefaultText => DefaultTextValue.Value;

    /// <summary>The built-in template, parsed.</summary>
    public static AuditTemplate Default => DefaultValue.Value;

    private static readonly IReadOnlySet<AuditPlaceholder> NameOfAuditTable = new HashSet<AuditPlaceholder> { AuditPlaceholder.Table, AuditPlaceholder.Owner };

    private static readonly IReadOnlySet<AuditPlaceholder> NameOfTrigger =
        new HashSet<AuditPlaceholder> { AuditPlaceholder.Table, AuditPlaceholder.Owner, AuditPlaceholder.AuditTable };

    private static readonly IReadOnlySet<AuditPlaceholder> AllNames =
        new HashSet<AuditPlaceholder> { AuditPlaceholder.Table, AuditPlaceholder.Owner, AuditPlaceholder.AuditTable, AuditPlaceholder.Trigger };

    /// <summary>Parses a template; throws <see cref="AuditTemplateException"/> with all errors found.</summary>
    public static AuditTemplate Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        var errors = new List<AuditTemplateError>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        IReadOnlyList<PatternPart>? auditTable = null, trigger = null, tableComment = null, source = null;
        AuditBaseColumns? baseColumns = null;
        var before = new List<ColumnInfo>();
        var after = new List<ColumnInfo>();
        var inColumns = false;

        for (var n = 0; n < lines.Length && source is null; n++)
        {
            var line = lines[n];
            var number = n + 1;
            if (Directive().Match(line) is { Success: true } directive)
            {
                var name = directive.Groups["name"].Value;
                var value = directive.Groups["value"].Value.Trim();
                if (name is "audit-table" or "trigger" or "table-comment" or "columns" or "base-columns" or "trigger-source" && !seen.Add(name))
                {
                    errors.Add(new(number, TextFormat.Format(AuditText.DirectiveRepeated, name)));
                }

                inColumns = false;
                switch (name)
                {
                    case "audit-table":
                        auditTable = NameValue(NameOfAuditTable);
                        break;
                    case "trigger":
                        trigger = NameValue(NameOfTrigger);
                        break;
                    case "table-comment":
                        tableComment = NameValue(AllNames);
                        break;
                    case "columns":
                        NoValue();
                        inColumns = true;
                        break;
                    case "base-columns":
                        baseColumns = value switch
                        {
                            "as-base" => AuditBaseColumns.AsBase,
                            "nullable" => AuditBaseColumns.Nullable,
                            _ => null,
                        };
                        if (baseColumns is null)
                        {
                            errors.Add(new(number, AuditText.BaseColumnsMode));
                            baseColumns = AuditBaseColumns.Nullable; // reported; go on to find further errors
                        }

                        inColumns = true;
                        break;
                    case "trigger-source":
                        NoValue();
                        source = TriggerSource(lines, n + 1, errors);
                        break;
                    default:
                        errors.Add(new(number, TextFormat.Format(AuditText.UnknownDirective, name)));
                        break;
                }

                IReadOnlyList<PatternPart> NameValue(IReadOnlySet<AuditPlaceholder> allowed)
                {
                    if (value.Length == 0)
                    {
                        errors.Add(new(number, TextFormat.Format(AuditText.DirectiveNeedsValue, name)));
                    }

                    return AuditPattern.Parse(value, number, name, allowed, errors);
                }

                void NoValue()
                {
                    if (value.Length > 0)
                    {
                        errors.Add(new(number, TextFormat.Format(AuditText.DirectiveTakesNoValue, name)));
                    }
                }

                continue;
            }

            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            if (!inColumns)
            {
                errors.Add(new(number, AuditText.TextOutsideSection));
                continue;
            }

            if (ParseColumn(line, number, errors) is { } column)
            {
                if (before.Concat(after).Any(c => c.Name == column.Name))
                {
                    errors.Add(new(number, TextFormat.Format(AuditText.ColumnRepeated, column.Name)));
                }
                else
                {
                    (baseColumns is null ? before : after).Add(column);
                }
            }
        }

        foreach (var (missing, present) in new[] { ("audit-table", auditTable is not null), ("trigger", trigger is not null), ("base-columns", baseColumns is not null), ("trigger-source", source is not null) })
        {
            if (!present)
            {
                errors.Add(new(0, TextFormat.Format(AuditText.DirectiveMissing, missing)));
            }
        }

        if (errors.Count > 0)
        {
            throw new AuditTemplateException(errors);
        }

        return new AuditTemplate(text, auditTable!, trigger!, tableComment, before, baseColumns!.Value, after, source!);
    }

    /// <summary>The rest of the file from <paramref name="start"/> on, without blank lines around it and a final <c>/</c> line.</summary>
    private static List<PatternPart> TriggerSource(string[] lines, int start, List<AuditTemplateError> errors)
    {
        var directiveLine = start; // 1-based line of the directive = index of the line after it
        var end = lines.Length;
        while (end > start && lines[end - 1].Trim().Length == 0)
        {
            end--;
        }

        if (end > start && lines[end - 1].Trim() == "/")
        {
            end--;
            while (end > start && lines[end - 1].Trim().Length == 0)
            {
                end--;
            }
        }

        while (start < end && lines[start].Trim().Length == 0)
        {
            start++;
        }

        if (start == end)
        {
            errors.Add(new(directiveLine, AuditText.TriggerSourceEmpty));
            return [];
        }

        var text = string.Join("\n", lines[start..end].Select(l => l.TrimEnd()));
        return [.. AuditPattern.Parse(text, start + 1, null, AllNames, errors)];
    }

    /// <summary>
    /// <c>NAME TYPE [GENERATED [ALWAYS | BY DEFAULT [ON NULL]] AS IDENTITY | DEFAULT [ON NULL] expr] [NOT NULL | NULL] [-- comment]</c>.
    /// </summary>
    private static ColumnInfo? ParseColumn(string line, int number, List<AuditTemplateError> errors)
    {
        var tokens = SqlTokenizer.Tokenize(line).Where(t => t.Kind != SqlTokenKind.Space).ToList();
        string? comment = null;
        if (tokens is [.., { Kind: SqlTokenKind.Comment } last] && last.Value.StartsWith("--", StringComparison.Ordinal))
        {
            comment = last.Value[2..].Trim() is { Length: > 0 } c ? c : null;
            tokens.RemoveAt(tokens.Count - 1);
        }

        tokens.RemoveAll(t => t.Kind == SqlTokenKind.Comment);
        if (tokens is not [{ Kind: SqlTokenKind.Word or SqlTokenKind.QuotedIdentifier } first, ..])
        {
            errors.Add(new(number, AuditText.ColumnNameMissing));
            return null;
        }

        var name = first.Kind == SqlTokenKind.Word ? first.Value.ToUpperInvariant() : first.Value;
        if (name.Length == 0)
        {
            errors.Add(new(number, AuditText.ColumnNameMissing));
            return null;
        }

        if (OracleIdentifier.Problem(name) is { } problem)
        {
            errors.Add(new(number, problem));
            return null;
        }

        // The type: up to the first keyword of the rest at bracket depth 0.
        var i = 1;
        var depth = 0;
        while (i < tokens.Count && !(depth == 0 && (tokens[i].IsWord("GENERATED") || tokens[i].IsWord("DEFAULT") || tokens[i].IsWord("NOT") || tokens[i].IsWord("NULL"))))
        {
            depth += tokens[i].IsSymbol("(") ? 1 : tokens[i].IsSymbol(")") ? -1 : 0;
            i++;
        }

        if (i == 1)
        {
            errors.Add(new(number, TextFormat.Format(AuditText.ColumnTypeMissing, name)));
            return null;
        }

        var typeText = line[tokens[1].Start..tokens[i - 1].End];
        if (!OracleTypes.TryParse(typeText, out var type))
        {
            errors.Add(new(number, TextFormat.Format(AuditText.ColumnTypeUnknown, name, typeText)));
            return null;
        }

        var identity = false;
        var onNull = false;
        string? defaultValue = null;
        bool? nullable = null;
        var rest = tokens.Skip(i).ToList();
        var k = 0;
        bool Word(string word)
        {
            if (k < rest.Count && rest[k].IsWord(word))
            {
                k++;
                return true;
            }

            return false;
        }

        if (Word("GENERATED"))
        {
            if (!Word("ALWAYS") && Word("BY"))
            {
                identity = Word("DEFAULT");
                onNull = identity && Word("ON") && Word("NULL");
            }
            else
            {
                identity = true;
            }

            identity = identity && Word("AS") && Word("IDENTITY");
            if (!identity)
            {
                return NotUnderstood();
            }
        }
        else if (Word("DEFAULT"))
        {
            if (k + 1 < rest.Count && rest[k].IsWord("ON") && rest[k + 1].IsWord("NULL"))
            {
                onNull = true;
                k += 2;
            }
            var expression = rest.Skip(k).ToList();
            var tail = expression is [.., var a, var b] && a.IsWord("NOT") && b.IsWord("NULL") ? 2
                : expression is [_, .., var c] && c.IsWord("NULL") ? 1
                : 0;
            expression = expression[..^tail];
            defaultValue = expression.Count == 0 ? null : line[expression[0].Start..expression[^1].End];
            if (defaultValue is null || !OracleDdl.FitsInStatement(defaultValue))
            {
                errors.Add(new(number, TextFormat.Format(AuditText.ColumnDefaultInvalid, name)));
                return null;
            }

            k += expression.Count;
        }

        if (Word("NOT"))
        {
            if (!Word("NULL"))
            {
                return NotUnderstood();
            }

            nullable = false;
        }
        else if (Word("NULL"))
        {
            nullable = true;
        }

        if (k < rest.Count)
        {
            return NotUnderstood();
        }

        return type with
        {
            Name = name,
            Nullable = !identity && !onNull && nullable != false,
            IsIdentity = identity,
            Default = defaultValue,
            DefaultOnNull = onNull,
            Comment = comment,
        };

        ColumnInfo? NotUnderstood()
        {
            var at = Math.Min(k, rest.Count - 1);
            errors.Add(new(number, TextFormat.Format(AuditText.ColumnNotUnderstood, name, at < 0 ? "" : line[rest[at].Start..rest[^1].End])));
            return null;
        }
    }

    [GeneratedRegex(@"^\s*--\s*@(?<name>[A-Za-z][A-Za-z-]*)(\s+(?<value>.*))?\s*$")]
    private static partial Regex Directive();
}
