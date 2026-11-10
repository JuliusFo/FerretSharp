namespace FerretSharp.Core.Ddl;

/// <summary>
/// The order of DDL steps and the script they make (R4b): one place for the schema comparison (WP-20), the designer
/// (WP-23) and what builds on them – before, each had its own order and its own way of writing the script.
/// </summary>
public static class DdlScript
{
    /// <summary>
    /// When a step of this kind runs. Constraints go first (a column of a dropped constraint may then be dropped), renamed
    /// constraints and their indexes next (their old names become free), then dropped and renamed columns (their names
    /// become free), new tables, changed columns (by their new names), new columns, keys before checks before foreign keys
    /// (one may reference a key added just before), checks they replace, indexes, comments (all names exist by then), PL/SQL
    /// objects and views (they may use any of it), synonyms (they point at any of it) and grants (on any of it), notes last. Steps of one stage keep the order they were written in – a column's
    /// type, default and NULL steps belong together.
    /// </summary>
    public static int Stage(DdlStepKind kind) => kind switch
    {
        DdlStepKind.DropConstraint => 0,
        DdlStepKind.RenameConstraint or DdlStepKind.RenameIndex => 1,
        DdlStepKind.DropColumn => 2,
        DdlStepKind.RenameColumn => 3,
        DdlStepKind.CreateTable => 4,
        DdlStepKind.ModifyType or DdlStepKind.ModifyDefault or DdlStepKind.ModifyNull => 5,
        DdlStepKind.AddColumn => 6,
        DdlStepKind.AddKey => 7,
        DdlStepKind.AddCheck => 8,
        DdlStepKind.AddForeignKey => 9,
        DdlStepKind.ReplaceConstraint => 10,
        DdlStepKind.DropIndex or DdlStepKind.CreateIndex => 11,
        DdlStepKind.Comment => 12,
        DdlStepKind.PlSqlObject or DdlStepKind.Synonym or DdlStepKind.Grant or DdlStepKind.CreateView or DdlStepKind.ViewComment => 13,
        _ => 14,
    };

    /// <summary>The steps in the order they run: by <see cref="Stage"/>, then by object (ordinal), then as written.</summary>
    public static IReadOnlyList<DdlStep> Order(IEnumerable<DdlStep> steps) => Order(steps, _ => 0);

    /// <summary>
    /// As <see cref="Order(IEnumerable{DdlStep})"/>, with <paramref name="rank"/> between stage and object: views that use
    /// other views of the script after them (WP-82).
    /// </summary>
    public static IReadOnlyList<DdlStep> Order(IEnumerable<DdlStep> steps, Func<DdlStep, int> rank) =>
        steps.Select((step, i) => (Step: step, Index: i))
            .OrderBy(s => Stage(s.Step.Kind))
            .ThenBy(s => rank(s.Step))
            .ThenBy(s => s.Step.Object, StringComparer.Ordinal)
            .ThenBy(s => s.Index)
            .Select(s => s.Step)
            .ToList();

    /// <summary>
    /// The steps as a script, in the order given: a blank line before each step, its warnings as <c>-- ⚠</c> comments, the
    /// statement ending with <c>;</c> – a PL/SQL object with a line holding only <c>/</c>. A step that must not run is
    /// commented out on every line (no <c>;</c>), so a script runner never executes it. Lines end with <c>\n</c>.
    /// </summary>
    /// <param name="header">Comment lines above the steps, as written (each starting with <c>--</c>).</param>
    public static string Render(IEnumerable<DdlStep> steps, IEnumerable<string>? header = null)
    {
        ArgumentNullException.ThrowIfNull(steps);
        // A header names tables, and a quoted Oracle name may hold a line break: every physical line stays a comment – the
        // rest of the name was an executable line in a script opened in the SQL editor (review 3.54.0, A11).
        var lines = header?.SelectMany(h => h.ReplaceLineEndings("\n").Split('\n').Select((line, i) => i == 0 ? line : "-- " + line)).ToList() ?? [];
        foreach (var step in steps)
        {
            if (lines.Count > 0)
            {
                lines.Add("");
            }

            lines.AddRange(CommentLines(step.Warning).Select(warning => $"-- ⚠ {warning}"));
            lines.AddRange(Lines(Text(step)));
            if (step.IsExecutable)
            {
                if (step.Kind == DdlStepKind.PlSqlObject)
                {
                    lines.Add("/");
                }
                else if (EndsInLineComment(step.Sql))
                {
                    // A view's text as Oracle keeps it may end with "-- …": a ";" there would be part of the comment, and the
                    // SQL editor would join the next statement to this one (review WP-82).
                    lines.Add(";");
                }
                else
                {
                    lines[^1] += ";";
                }
            }
        }

        return string.Join("\n", lines);
    }

    /// <summary>The step as it stands in a script, without terminator: commented out on every line if it must not run.</summary>
    public static string Text(DdlStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        return step.IsExecutable ? step.Sql : Commented(step.Sql);
    }

    /// <summary>The last token of <paramref name="sql"/> other than spaces is a <c>--</c> comment.</summary>
    private static bool EndsInLineComment(string sql) =>
        Query.SqlTokenizer.Tokenize(sql).LastOrDefault(t => t.Kind != Query.SqlTokenKind.Space) is { Kind: Query.SqlTokenKind.Comment } last
        && sql.AsSpan(last.Start).StartsWith("--", StringComparison.Ordinal);

    /// <summary>Comments every line that is not a comment yet.</summary>
    private static string Commented(string sql) =>
        string.Join("\n", CommentLines(sql).Select(line => line.StartsWith("--", StringComparison.Ordinal) ? line : "-- " + line));

    /// <summary>A statement's lines: CRLF and LF end one, a lone CR inside a quoted name or literal stays (it is part of the value).</summary>
    private static IEnumerable<string> Lines(string? text) =>
        text is null ? [] : text.Split('\n').Select(line => line.TrimEnd('\r'));

    /// <summary>
    /// The lines of a comment (a warning, a step that must not run): split at every line end – CR, NEL, LS and PS too. The
    /// SQL editor breaks lines there, and the rest of a quoted name in a note became an executable line (review of the
    /// column ADD script, finding 10; the header since review 3.54.0, A11).
    /// </summary>
    private static IEnumerable<string> CommentLines(string? text) =>
        text is null ? [] : text.ReplaceLineEndings("\n").Split('\n');
}
