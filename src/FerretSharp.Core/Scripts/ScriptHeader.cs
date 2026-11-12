using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using FerretSharp.Core.Oracle;
using FerretSharp.Core.Query;
using FerretSharp.Core.Resources;

namespace FerretSharp.Core.Scripts;

/// <summary>A parameter of a SQL script (WP-77): a bind of its text with the type it is bound as – never a value.</summary>
/// <param name="List">
/// <c>-- @param name type list</c> (WP-84, wish of the user 2026-11-11): the bind stands alone in <c>IN ( … )</c> and takes the
/// values of several marked rows, one bind each (<see cref="SqlBindLists"/>); with one value it is a plain bind.
/// </param>
public sealed record ScriptParameter(string Name, SqlVariableType Type, bool List = false);

/// <summary>
/// The column a parameter of a SQL script takes its value from (WP-84, <c>-- @target name TABLE.COLUMN</c>): the script is
/// offered in the cell menu of that column and of every column that holds the same values (<see cref="ScriptTargets"/>).
/// </summary>
/// <param name="Parameter">The parameter's name as in its <c>@param</c>.</param>
/// <param name="Table">The table or view without owner, as the dictionary has it (<c>kunde</c> → <c>KUNDE</c>).</param>
/// <param name="Column">The column, as the dictionary has it.</param>
public sealed record ScriptTarget(string Parameter, string Table, string Column);

/// <summary>A line of a script's header that cannot be used, with its number in the file (1-based).</summary>
public sealed record ScriptHeaderError(int Line, string Message)
{
    public override string ToString() => TextFormat.Format(ScriptText.ErrorInLine, Line, Message);
}

/// <summary>
/// The comment lines at the top of a SQL script (WP-77, ADR 0046): <c>-- @description …</c>, <c>-- @param name type</c> and
/// <c>-- @target name TABLE.COLUMN</c> (WP-84).
/// Lines it does not know (another directive, a <c>@param</c> with errors) stay as they are in <see cref="Other"/> and are
/// written back unchanged, so a newer FerretSharp's or a colleague's lines are not lost.
/// </summary>
/// <param name="Description">The description, its lines joined by line breaks; null without one.</param>
/// <param name="Parameters">The parameters in the order of the file.</param>
/// <param name="Other">Header lines kept as they are (without line break).</param>
public sealed record ScriptHeader(string? Description, IReadOnlyList<ScriptParameter> Parameters, IReadOnlyList<string> Other)
{
    public static ScriptHeader Empty { get; } = new(null, [], []);

    /// <summary>
    /// The columns the parameters take their values from (WP-84), in the order of the file; each names a parameter of
    /// <see cref="Parameters"/>. Not in the constructor: a link in a workspace file written before WP-84 has none.
    /// </summary>
    public IReadOnlyList<ScriptTarget> Targets { get; init; } = [];

    public bool IsEmpty => string.IsNullOrWhiteSpace(Description) && Parameters.Count == 0 && Targets.Count == 0 && Other.Count == 0;

    /// <summary>The same lines (records compare lists by reference).</summary>
    public bool SameAs(ScriptHeader other) =>
        Description == other.Description && Parameters.SequenceEqual(other.Parameters) && Targets.SequenceEqual(other.Targets)
        && Other.SequenceEqual(other.Other);
}

/// <summary>A script split into its header and the SQL below it.</summary>
/// <param name="Body">The text below the header, with <c>\n</c> line breaks; what a SQL tab shows.</param>
/// <param name="Errors">Header lines that could not be used (kept in <see cref="ScriptHeader.Other"/>).</param>
public sealed record ParsedScript(ScriptHeader Header, string Body, IReadOnlyList<ScriptHeaderError> Errors);

/// <summary>Reads and writes the header of a SQL script (WP-77). Syntax: <c>docs/sql-scripts.md</c>.</summary>
public static partial class ScriptHeaders
{
    /// <summary>The type names of <c>@param</c>, as written.</summary>
    private static readonly IReadOnlyDictionary<string, SqlVariableType> TypeNames = new Dictionary<string, SqlVariableType>(StringComparer.OrdinalIgnoreCase)
    {
        ["text"] = SqlVariableType.Text,
        ["char"] = SqlVariableType.Char,
        ["number"] = SqlVariableType.Number,
        ["date"] = SqlVariableType.Date,
        ["raw"] = SqlVariableType.Raw,
        ["null"] = SqlVariableType.Null,
    };

    /// <summary>The name <c>@param</c> writes for a type.</summary>
    public static string TypeName(SqlVariableType type) => type.ToString().ToLowerInvariant();

    /// <summary>
    /// Splits <paramref name="text"/> into header and body. The header is the run of <c>-- @…</c> lines at the very top
    /// (blank lines before them allowed); one blank line after it belongs to it. Never throws: a line with errors is
    /// reported and kept.
    /// </summary>
    public static ParsedScript Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var lines = text.ReplaceLineEndings("\n").Split('\n');
        var first = 0;
        while (first < lines.Length && lines[first].Trim().Length == 0)
        {
            first++;
        }

        if (first >= lines.Length || !Directive().IsMatch(lines[first]))
        {
            return new ParsedScript(ScriptHeader.Empty, text.ReplaceLineEndings("\n"), []);
        }

        string? description = null;
        var parameters = new List<ScriptParameter>();
        var targets = new List<(ScriptTarget Target, int Line)>();
        var other = new List<string>();
        var errors = new List<ScriptHeaderError>();
        var n = first;
        for (; n < lines.Length && Directive().Match(lines[n]) is { Success: true } directive; n++)
        {
            var name = directive.Groups["name"].Value;
            var value = directive.Groups["value"].Value.Trim();
            switch (name.ToLowerInvariant())
            {
                case "description":
                    description = description is null ? value : description + "\n" + value;
                    break;
                case "param" when Parameter(value, n + 1, errors) is { } parameter:
                    if (parameters.Any(p => string.Equals(p.Name, parameter.Name, StringComparison.OrdinalIgnoreCase)))
                    {
                        errors.Add(new(n + 1, TextFormat.Format(ScriptText.ParamRepeated, parameter.Name)));
                        other.Add(lines[n]);
                    }
                    else
                    {
                        parameters.Add(parameter);
                    }

                    break;
                case "target":
                    if (Target(value, n + 1, errors) is { } target)
                    {
                        targets.Add((target, n + 1));
                    }
                    else
                    {
                        other.Add(lines[n]);
                    }

                    break;
                default:
                    other.Add(lines[n]); // unknown or with errors: kept
                    break;
            }
        }

        // Only now: a @target may stand above its @param.
        var known = new List<ScriptTarget>();
        foreach (var (target, line) in targets)
        {
            if (parameters.FirstOrDefault(p => string.Equals(p.Name, target.Parameter, StringComparison.OrdinalIgnoreCase)) is not { } parameter)
            {
                errors.Add(new(line, TextFormat.Format(ScriptText.TargetUnknownParameter, target.Parameter)));
                other.Add(lines[line - 1]);
            }
            else if (known.Any(k => string.Equals(k.Parameter, parameter.Name, StringComparison.OrdinalIgnoreCase) && k.Table == target.Table && k.Column == target.Column))
            {
                errors.Add(new(line, TextFormat.Format(ScriptText.TargetRepeated, target.Table + "." + target.Column)));
                other.Add(lines[line - 1]);
            }
            else
            {
                known.Add(target with { Parameter = parameter.Name });
            }
        }

        errors.Sort((a, b) => a.Line.CompareTo(b.Line));

        if (n < lines.Length && lines[n].Trim().Length == 0)
        {
            n++;
        }

        var body = string.Join('\n', lines[n..]);
        return new ParsedScript(
            new ScriptHeader(string.IsNullOrWhiteSpace(description) ? null : description, parameters, other) { Targets = known }, body, errors);
    }

    /// <summary>
    /// The file's text: the header's lines (description, parameters, targets, then the kept ones), a blank line and the body. Line
    /// breaks are <c>\n</c>; the file keeps its own (<see cref="ScriptFiles"/>).
    /// </summary>
    public static string Compose(ScriptHeader header, string body)
    {
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(body);
        body = body.ReplaceLineEndings("\n");
        // Only lines that are header lines: one from a workspace file edited by hand ("GRANT …") would end up as SQL in the
        // shared script (review WP-77, K1).
        var other = header.Other.Select(OneLine).Where(l => Directive().IsMatch(l)).ToList();
        if (string.IsNullOrWhiteSpace(header.Description) && header.Parameters.Count == 0 && header.Targets.Count == 0 && other.Count == 0)
        {
            return body;
        }

        var text = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(header.Description))
        {
            foreach (var line in header.Description.ReplaceLineEndings("\n").Split('\n'))
            {
                text.Append("-- @description ").Append(OneLine(line).Trim()).Append('\n');
            }
        }

        foreach (var parameter in header.Parameters)
        {
            text.Append("-- @param ").Append(ParameterName(parameter.Name)).Append(' ').Append(TypeName(parameter.Type))
                .Append(parameter.List ? " list" : "").Append('\n');
        }

        // A target whose parameter is gone (its bind left the text) is still written: the next read reports it, nothing is lost.
        foreach (var target in header.Targets)
        {
            text.Append("-- @target ").Append(ParameterName(OneLine(target.Parameter))).Append(' ')
                .Append(TargetName(target.Table)).Append('.').Append(TargetName(target.Column)).Append('\n');
        }

        foreach (var line in other)
        {
            text.Append(line).Append('\n');
        }

        // A body that starts with a blank line would lose it on the next read: the blank line after the header is the header's.
        return text.Append('\n').Append(body).ToString();
    }

    /// <summary>
    /// The parameters of <paramref name="body"/>: its binds in order of use, each with the type of its variable in
    /// <paramref name="variables"/> (text without one). Variables of binds no longer in the text are left out.
    /// </summary>
    /// <param name="previous">The parameters of the file: a <see cref="ScriptParameter.List"/> stays one (a variable has no such mark).</param>
    public static IReadOnlyList<ScriptParameter> ParametersOf(string body, IReadOnlyList<SqlVariable> variables, IReadOnlyList<ScriptParameter>? previous = null)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(variables);
        var binds = SqlScript.Split(body).SelectMany(s => SqlScript.Analyze(s.Text).Binds).Distinct(StringComparer.OrdinalIgnoreCase);
        return
        [
            .. binds.Select(b => new ScriptParameter(
                b, variables.FirstOrDefault(v => string.Equals(v.Name, b, StringComparison.OrdinalIgnoreCase))?.Type ?? SqlVariableType.Text,
                previous?.Any(p => p.List && string.Equals(p.Name, b, StringComparison.OrdinalIgnoreCase)) == true)),
        ];
    }

    /// <summary>
    /// <paramref name="variables"/> with a variable for each parameter not there yet (type from the header, no value): the
    /// tab's values stay (decision of the user, 2026-11-08).
    /// </summary>
    public static IReadOnlyList<SqlVariable> AddTo(IReadOnlyList<SqlVariable> variables, IEnumerable<ScriptParameter> parameters)
    {
        ArgumentNullException.ThrowIfNull(variables);
        ArgumentNullException.ThrowIfNull(parameters);
        var result = variables.ToList();
        foreach (var parameter in parameters)
        {
            if (!result.Any(v => string.Equals(v.Name, parameter.Name, StringComparison.OrdinalIgnoreCase)))
            {
                result.Add(new SqlVariable(parameter.Name, parameter.Type, ""));
            }
        }

        return result;
    }

    /// <summary>
    /// <paramref name="variables"/> with the types of <paramref name="parameters"/> – for a tab that shows the file (opened,
    /// reloaded after a change on disk): the file decides the types, the values typed stay; missing ones are added.
    /// </summary>
    public static IReadOnlyList<SqlVariable> Apply(IReadOnlyList<SqlVariable> variables, IReadOnlyList<ScriptParameter> parameters)
    {
        ArgumentNullException.ThrowIfNull(variables);
        ArgumentNullException.ThrowIfNull(parameters);
        var typed = variables.Select(v => parameters.FirstOrDefault(p => string.Equals(p.Name, v.Name, StringComparison.OrdinalIgnoreCase)) is { } p && p.Type != v.Type
            ? v with { Type = p.Type, National = false }
            : v).ToList();
        return AddTo(typed, parameters);
    }

    /// <summary>A line of the header holds no line break: one in a description would end the header.</summary>
    private static string OneLine(string line) => line.Replace('\r', ' ').Replace('\n', ' ');

    /// <summary>A name with characters a bind written without quotes cannot have, in quotes (as in the SQL: <c>:"a b"</c>).</summary>
    private static string ParameterName(string name) =>
        name.Length > 0 && name.All(c => char.IsLetterOrDigit(c) || c is '_' or '$' or '#') ? name : "\"" + name.Replace("\"", "", StringComparison.Ordinal) + "\"";

    /// <summary>A table or column of a target as written: plain if Oracle takes it so, else in quotes.</summary>
    private static string TargetName(string name) => OracleIdentifier.QuoteIfNeeded(OneLine(name).Replace("\"", "", StringComparison.Ordinal));

    private static ScriptTarget? Target(string value, int line, List<ScriptHeaderError> errors)
    {
        if (TargetValue().Match(value) is not { Success: true } match)
        {
            errors.Add(new(line, ScriptText.TargetSyntax));
            return null;
        }

        var name = match.Groups["quoted"].Success ? match.Groups["quoted"].Value : match.Groups["plain"].Value;
        return new ScriptTarget(name, OracleIdentifier.Normalize(match.Groups["table"].Value), OracleIdentifier.Normalize(match.Groups["column"].Value));
    }

    private static ScriptParameter? Parameter(string value, int line, List<ScriptHeaderError> errors)
    {
        if (ParamValue().Match(value) is not { Success: true } match)
        {
            errors.Add(new(line, ScriptText.ParamSyntax));
            return null;
        }

        var name = match.Groups["quoted"].Success ? match.Groups["quoted"].Value : match.Groups["plain"].Value;
        var typeName = match.Groups["type"].Value;
        if (!TypeNames.TryGetValue(typeName, out var type))
        {
            errors.Add(new(line, TextFormat.Format(ScriptText.ParamType, typeName,
                string.Join(", ", TypeNames.Keys.Select(k => k.ToString(CultureInfo.InvariantCulture))))));
            return null;
        }

        return new ScriptParameter(name, type, match.Groups["list"].Success);
    }

    [GeneratedRegex(@"^\s*--\s*@(?<name>[A-Za-z][A-Za-z-]*)(\s+(?<value>.*))?\s*$")]
    private static partial Regex Directive();

    [GeneratedRegex(@"^:?(?:""(?<quoted>[^""]+)""|(?<plain>[\p{L}\p{N}_$#]+))\s+(?<type>\S+)(?:\s+(?<list>[Ll][Ii][Ss][Tt]))?$")]
    private static partial Regex ParamValue();

    /// <summary>Parameter, then <c>TABLE.COLUMN</c> – each name plain or in quotes, no owner (a script runs on any schema).</summary>
    [GeneratedRegex(@"^:?(?:""(?<quoted>[^""]+)""|(?<plain>[\p{L}\p{N}_$#]+))\s+(?<table>""[^""]+""|[\p{L}_$#][\p{L}\p{N}_$#]*)\s*\.\s*(?<column>""[^""]+""|[\p{L}_$#][\p{L}\p{N}_$#]*)$")]
    private static partial Regex TargetValue();
}
