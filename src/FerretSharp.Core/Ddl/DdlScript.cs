namespace FerretSharp.Core.Ddl;

/// <summary>
/// The order of DDL steps and the script they make (R4b): one place for the schema comparison (WP-20), the designer
/// (WP-23) and what builds on them – before, each had its own order and its own way of writing the script.
/// </summary>
public static class DdlScript
{
    /// <summary>
    /// When a step of this kind runs. Constraints go first (a column of a dropped constraint may then be dropped), then
    /// dropped and renamed columns (their names become free), new tables, changed columns (by their new names), new columns,
    /// keys before checks before foreign keys (one may reference a key added just before), indexes, comments (all names
    /// exist by then), PL/SQL objects (they may use any of it), notes last. Steps of one stage keep the order they were
    /// written in – a column's type, default and NULL steps belong together.
    /// </summary>
    public static int Stage(DdlStepKind kind) => kind switch
    {
        DdlStepKind.DropConstraint => 0,
        DdlStepKind.DropColumn => 1,
        DdlStepKind.RenameColumn => 2,
        DdlStepKind.CreateTable => 3,
        DdlStepKind.ModifyType or DdlStepKind.ModifyDefault or DdlStepKind.ModifyNull => 4,
        DdlStepKind.AddColumn => 5,
        DdlStepKind.AddKey => 6,
        DdlStepKind.AddCheck => 7,
        DdlStepKind.AddForeignKey => 8,
        DdlStepKind.DropIndex or DdlStepKind.CreateIndex => 9,
        DdlStepKind.Comment => 10,
        DdlStepKind.PlSqlObject => 11,
        _ => 12,
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
