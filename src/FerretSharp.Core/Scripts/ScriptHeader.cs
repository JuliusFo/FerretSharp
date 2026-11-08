using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using FerretSharp.Core.Query;
using FerretSharp.Core.Resources;

namespace FerretSharp.Core.Scripts;

/// <summary>A parameter of a SQL script (WP-77): a bind of its text with the type it is bound as – never a value.</summary>
public sealed record ScriptParameter(string Name, SqlVariableType Type);

/// <summary>A line of a script's header that cannot be used, with its number in the file (1-based).</summary>
public sealed record ScriptHeaderError(int Line, string Message)
{
    public override string ToString() => TextFormat.Format(ScriptText.ErrorInLine, Line, Message);
}

/// <summary>
/// The comment lines at the top of a SQL script (WP-77, ADR 0046): <c>-- @description …</c> and <c>-- @param name type</c>.
/// Lines it does not know (another directive, a <c>@param</c> with errors) stay as they are in <see cref="Other"/> and are
/// written back unchanged, so a newer FerretSharp's or a colleague's lines are not lost.
/// </summary>
/// <param name="Description">The description, its lines joined by line breaks; null without one.</param>
/// <param name="Parameters">The parameters in the order of the file.</param>
/// <param name="Other">Header lines kept as they are (without line break).</param>
public sealed record ScriptHeader(string? Description, IReadOnlyList<ScriptParameter> Parameters, IReadOnlyList<string> Other)
{
    public static ScriptHeader Empty { get; } = new(null, [], []);

    public bool IsEmpty => string.IsNullOrWhiteSpace(Description) && Parameters.Count == 0 && Other.Count == 0;

    /// <summary>The same lines (records compare lists by reference).</summary>
    public bool SameAs(ScriptHeader other) =>
        Description == other.Description && Parameters.SequenceEqual(other.Parameters) && Other.SequenceEqual(other.Other);
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
                default:
                    other.Add(lines[n]); // unknown or with errors: kept
                    break;
            }
        }

        if (n < lines.Length && lines[n].Trim().Length == 0)
        {
            n++;
        }

        var body = string.Join('\n', lines[n..]);
        return new ParsedScript(new ScriptHeader(string.IsNullOrWhiteSpace(description) ? null : description, parameters, other), body, errors);
    }

    /// <summary>
    /// The file's text: the header's lines (description, parameters, then the kept ones), a blank line and the body. Line
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
        if (string.IsNullOrWhiteSpace(header.Description) && header.Parameters.Count == 0 && other.Count == 0)
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
            text.Append("-- @param ").Append(ParameterName(parameter.Name)).Append(' ').Append(TypeName(parameter.Type)).Append('\n');
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
    public static IReadOnlyList<ScriptParameter> ParametersOf(string body, IReadOnlyList<SqlVariable> variables)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(variables);
        var binds = SqlScript.Split(body).SelectMany(s => SqlScript.Analyze(s.Text).Binds).Distinct(StringComparer.OrdinalIgnoreCase);
        return
        [
            .. binds.Select(b => new ScriptParameter(
                b, variables.FirstOrDefault(v => string.Equals(v.Name, b, StringComparison.OrdinalIgnoreCase))?.Type ?? SqlVariableType.Text)),
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

        return new ScriptParameter(name, type);
    }

    [GeneratedRegex(@"^\s*--\s*@(?<name>[A-Za-z][A-Za-z-]*)(\s+(?<value>.*))?\s*$")]
    private static partial Regex Directive();

    [GeneratedRegex(@"^:?(?:""(?<quoted>[^""]+)""|(?<plain>[\p{L}\p{N}_$#]+))\s+(?<type>\S+)$")]
    private static partial Regex ParamValue();
}
