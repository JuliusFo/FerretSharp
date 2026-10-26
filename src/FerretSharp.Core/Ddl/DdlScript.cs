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
    /// objects (they may use any of it), notes last. Steps of one stage keep the order they were written in – a column's
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
        DdlStepKind.PlSqlObject => 13,
        _ => 14,
    };

    /// <summary>The steps in the order they run: by <see cref="Stage"/>, then by object (ordinal), then as written.</summary>
    public static IReadOnlyList<DdlStep> Order(IEnumerable<DdlStep> steps) =>
        steps.Select((step, i) => (Step: step, Index: i))
            .OrderBy(s => Stage(s.Step.Kind))
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
        var lines = header?.ToList() ?? [];
        foreach (var step in steps)
        {
            if (lines.Count > 0)
            {
                lines.Add("");
            }

            lines.AddRange(Lines(step.Warning).Select(warning => $"-- ⚠ {warning}"));
            lines.AddRange(Lines(Text(step)));
            if (step.IsExecutable)
            {
                if (step.Kind == DdlStepKind.PlSqlObject)
                {
                    lines.Add("/");
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

    /// <summary>Comments every line that is not a comment yet.</summary>
    private static string Commented(string sql) =>
        string.Join("\n", Lines(sql).Select(line => line.StartsWith("--", StringComparison.Ordinal) ? line : "-- " + line));

    private static IEnumerable<string> Lines(string? text) =>
        text is null ? [] : text.Split('\n').Select(line => line.TrimEnd('\r'));
}
