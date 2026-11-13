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
    public override string ToString() => TextFormat.Format(CoreText.ErrorInLine, Line, Message);
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

/// <summary>What saving a SQL tab's text writes, and the tab afterwards (<see cref="ScriptHeaders.ForSave"/>).</summary>
/// <param name="Header">The header to write.</param>
/// <param name="Body">The SQL below it.</param>
/// <param name="Text">The tab's text after saving: as typed, unless lines left it or joined it.</param>
/// <param name="Variables">The tab's variables, with the types of the <c>@param</c> lines typed.</param>
/// <param name="Errors">Header lines that cannot be used, numbered as in the tab's text; they are written as they are.</param>
public sealed record ScriptSave(ScriptHeader Header, string Body, string Text, IReadOnlyList<SqlVariable> Variables, IReadOnlyList<ScriptHeaderError> Errors);

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
        var read = ReadLines(lines);
        if (read.End == 0)
        {
            return new ParsedScript(ScriptHeader.Empty, text.ReplaceLineEndings("\n"), []);
        }

        var other = read.Other.Select(o => o.Text).ToList();
        var errors = read.Errors.ToList();
        var targets = Checked(read.Targets, read.Parameters, lines, other, errors, ScriptText.TargetUnknownParameter);
        errors.Sort((a, b) => a.Line.CompareTo(b.Line));
        return new ParsedScript(
            new ScriptHeader(read.Description, read.Parameters, other) { Targets = targets }, string.Join('\n', lines[read.End..]), errors);
    }

    /// <summary>
    /// What saving a SQL tab's text writes (fix-script-header-in-editor, decision of the user 2026-11-13: the header lines
    /// FerretSharp has no other place for show in the editor). The <c>-- @…</c> lines at the top of the text are its
    /// header: <c>@target</c> and unknown lines as they stand; <c>@param</c> gives a parameter its type (and <c>list</c>),
    /// <c>@description</c> the description – both are taken out of the text, they live in the variables and the dialog.
    /// The rest is the body. A target is checked against the binds of the body, not against <c>@param</c> lines of the text.
    /// </summary>
    /// <param name="file">The header of the linked file; null for a new script.</param>
    /// <param name="fileHeaderInText">
    /// The tab shows the file's targets and unknown lines (<see cref="ScriptLink.HeaderInText"/>): its text is the truth, a
    /// line deleted there is gone. A tab of a workspace written before keeps the file's lines; they are put into its text.
    /// </param>
    public static ScriptSave ForSave(string text, IReadOnlyList<SqlVariable> variables, ScriptHeader? file, bool fileHeaderInText)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(variables);
        var lines = text.ReplaceLineEndings("\n").Split('\n');
        var read = ReadLines(lines);
        var body = string.Join('\n', lines[read.End..]);
        var typed = read.Parameters;
        var typedVariables = Apply(variables, typed);
        IReadOnlyList<ScriptParameter> previous = [.. typed, .. (file?.Parameters ?? []).Where(p => !typed.Any(t => SameName(t.Name, p.Name)))];
        var parameters = ParametersOf(body, typedVariables, previous);

        var other = read.Other.Select(o => o.Text).ToList();
        var errors = read.Errors.ToList();
        var targets = Checked(read.Targets, parameters, lines, other, errors, ScriptText.TargetUnknownBind);
        var added = false;
        if (!fileHeaderInText && file is not null)
        {
            foreach (var target in file.Targets.Where(t => !targets.Any(k => SameName(k.Parameter, t.Parameter) && k.Table == t.Table && k.Column == t.Column)))
            {
                targets.Add(target);
                added = true;
            }

            foreach (var line in file.Other.Where(l => !other.Contains(l, StringComparer.Ordinal)))
            {
                other.Add(line);
                added = true;
            }
        }

        errors.Sort((a, b) => a.Line.CompareTo(b.Line));
        var header = new ScriptHeader(read.Description ?? file?.Description, parameters, other) { Targets = targets };
        // Typed @param and @description lines leave the text; so do the file's lines join it. Otherwise it stays as typed
        // (the cursor stays where it is).
        var after = read.Taken > 0 || added ? EditorText(header, body) : text;
        return new ScriptSave(header, body, after, typedVariables, errors);
    }

    /// <summary>
    /// What a SQL tab shows of a script: the header lines FerretSharp has no other place for – targets, then the lines it
    /// keeps as they are – a blank line and the body. Without such lines the body alone.
    /// </summary>
    public static string EditorText(ScriptHeader header, string body)
    {
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(body);
        var shown = TargetLines(header.Targets).Concat(KeptLines(header.Other)).ToList();
        return shown.Count == 0 ? body : string.Join('\n', shown) + "\n\n" + body;
    }

    /// <summary>The header lines at the top of <paramref name="lines"/>, with what each one is; never throws.</summary>
    private static HeaderLines ReadLines(string[] lines)
    {
        var first = 0;
        while (first < lines.Length && lines[first].Trim().Length == 0)
        {
            first++;
        }

        if (first >= lines.Length || !Directive().IsMatch(lines[first]))
        {
            return new HeaderLines(null, [], [], [], [], 0, 0);
        }

        string? description = null;
        var parameters = new List<ScriptParameter>();
        var targets = new List<(ScriptTarget Target, int Line)>();
        var other = new List<(string Text, int Line)>();
        var errors = new List<ScriptHeaderError>();
        var taken = 0;
        var n = first;
        for (; n < lines.Length && Directive().Match(lines[n]) is { Success: true } directive; n++)
        {
            var name = directive.Groups["name"].Value;
            var value = directive.Groups["value"].Value.Trim();
            switch (name.ToLowerInvariant())
            {
                case "description":
                    description = description is null ? value : description + "\n" + value;
                    taken++;
                    break;
                case "param" when Parameter(value, n + 1, errors) is { } parameter:
                    if (parameters.Any(p => SameName(p.Name, parameter.Name)))
                    {
                        errors.Add(new(n + 1, TextFormat.Format(ScriptText.ParamRepeated, parameter.Name)));
                        other.Add((lines[n], n + 1));
                    }
                    else
                    {
                        parameters.Add(parameter);
                        taken++;
                    }

                    break;
                case "target":
                    if (Target(value, n + 1, errors) is { } target)
                    {
                        targets.Add((target, n + 1));
                    }
                    else
                    {
                        other.Add((lines[n], n + 1));
                    }

                    break;
                default:
                    other.Add((lines[n], n + 1)); // unknown or with errors: kept
                    break;
            }
        }

        if (n < lines.Length && lines[n].Trim().Length == 0)
        {
            n++;
        }

        return new HeaderLines(string.IsNullOrWhiteSpace(description) ? null : description, parameters, targets, other, errors, n, taken);
    }

    /// <summary>
    /// The targets whose parameter is among <paramref name="parameters"/>, named as there and each once – checked only after
    /// the whole header was read: a @target may stand above its @param. The others join <paramref name="other"/>, an unknown
    /// parameter reported with <paramref name="unknown"/> (a file needs its @param, a tab only the bind).
    /// </summary>
    private static List<ScriptTarget> Checked(
        IReadOnlyList<(ScriptTarget Target, int Line)> targets, IReadOnlyList<ScriptParameter> parameters, string[] lines, List<string> other, List<ScriptHeaderError> errors,
        string unknown)
    {
        var known = new List<ScriptTarget>();
        foreach (var (target, line) in targets)
        {
            if (parameters.FirstOrDefault(p => SameName(p.Name, target.Parameter)) is not { } parameter)
            {
                errors.Add(new(line, TextFormat.Format(unknown, target.Parameter)));
                other.Add(lines[line - 1]);
            }
            else if (known.Any(k => SameName(k.Parameter, parameter.Name) && k.Table == target.Table && k.Column == target.Column))
            {
                errors.Add(new(line, TextFormat.Format(ScriptText.TargetRepeated, target.Table + "." + target.Column)));
                other.Add(lines[line - 1]);
            }
            else
            {
                known.Add(target with { Parameter = parameter.Name });
            }
        }

        return known;
    }

    private static bool SameName(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <param name="Other">Lines kept as they are, with their number (1-based).</param>
    /// <param name="End">The index of the body's first line (one blank line after the header belongs to it).</param>
    /// <param name="Taken">How many lines gave the description or a parameter.</param>
    private sealed record HeaderLines(
        string? Description, List<ScriptParameter> Parameters, List<(ScriptTarget Target, int Line)> Targets, List<(string Text, int Line)> Other,
        List<ScriptHeaderError> Errors, int End, int Taken);

    /// <summary>
    /// The file's text: the header's lines (description, parameters, targets, then the kept ones), a blank line and the body. Line
    /// breaks are <c>\n</c>; the file keeps its own (<see cref="ScriptFiles"/>).
    /// </summary>
    public static string Compose(ScriptHeader header, string body)
    {
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(body);
        body = body.ReplaceLineEndings("\n");
        var other = KeptLines(header.Other).ToList();
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
        foreach (var line in TargetLines(header.Targets).Concat(other))
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
    /// <summary>
    /// Every separator the reader splits at (<see cref="string.ReplaceLineEndings()"/>: also NEL, U+2028, U+2029, form feed)
    /// as a space: a header line must stay one line, or its rest lands in the shared script's SQL (review 3.70.0, A9).
    /// </summary>
    private static string OneLine(string line) => line.ReplaceLineEndings(" ");

    private static IEnumerable<string> TargetLines(IEnumerable<ScriptTarget> targets) =>
        targets.Select(t => "-- @target " + ParameterName(t.Parameter) + " " + TargetName(t.Table) + "." + TargetName(t.Column));

    /// <summary>
    /// Only lines that are header lines: one from a workspace file edited by hand ("GRANT …") would end up as SQL in the
    /// shared script (review WP-77, K1) – or end the header in the editor.
    /// </summary>
    private static IEnumerable<string> KeptLines(IEnumerable<string> other) => other.Select(OneLine).Where(l => Directive().IsMatch(l));

    /// <summary>A name with characters a bind written without quotes cannot have, in quotes (as in the SQL: <c>:"a b"</c>).</summary>
    private static string ParameterName(string name) => QuotedName(OneLine(name));

    private static string QuotedName(string name) =>
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
