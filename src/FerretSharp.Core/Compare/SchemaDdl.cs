using System.Text;

namespace FerretSharp.Core.Compare;

/// <summary>A statement of the DDL proposal; never run by FerretSharp before WP-22.</summary>
/// <param name="Object">The table, view or materialized view it changes.</param>
/// <param name="Sql">One statement without trailing semicolon; commented out (<c>-- …</c>) when only a hint (e.g. DROP).</param>
/// <param name="Warning">Why this step needs care (data loss, may fail on existing data, …); null if none.</param>
public sealed record DdlStep(string Object, string Sql, string? Warning = null)
{
    /// <summary>Only a hint for the user (every line a comment), not a statement.</summary>
    public bool IsCommented => Sql.StartsWith("--", StringComparison.Ordinal);
}

/// <summary>The DDL that makes <see cref="Target"/> look like <see cref="Reference"/> (indexes into the comparison's sides).</summary>
public sealed record DdlProposal(int Reference, int Target, IReadOnlyList<DdlStep> Steps)
{
    /// <summary>Owner of the reference side, for the script header; null = only the side number is known.</summary>
    public string? ReferenceOwner { get; init; }

    /// <summary>Owner of the target side, for the script header; null = only the side number is known.</summary>
    public string? TargetOwner { get; init; }

    /// <summary>
    /// The steps as a script: statements ending with <c>;</c>, warnings as comments above them. Hints stay comments
    /// on every line (no <c>;</c>), so a script runner never executes them.
    /// </summary>
    public string Script
    {
        get
        {
            var script = new StringBuilder();
            script.AppendLine($"-- DDL-Vorschlag: {Side(TargetOwner, Target)} an die Referenz {Side(ReferenceOwner, Reference)} angleichen.");
            script.AppendLine("-- Von FerretSharp erzeugt, nicht ausgeführt: vor dem Ausführen prüfen. Auskommentiertes bewusst entscheiden.");
            if (Steps.Count == 0)
            {
                script.AppendLine("-- Keine Unterschiede.");
            }

            foreach (var step in Steps)
            {
                script.AppendLine();
                foreach (var warning in Lines(step.Warning))
                {
                    script.AppendLine($"-- ⚠ {warning}");
                }

                var lines = Lines(step.Sql).ToList();
                if (step.IsCommented)
                {
                    foreach (var line in lines)
                    {
                        script.AppendLine(line.StartsWith("--", StringComparison.Ordinal) ? line : "-- " + line);
                    }
                }
                else
                {
                    for (var i = 0; i < lines.Count; i++)
                    {
                        script.AppendLine(i == lines.Count - 1 ? lines[i] + ";" : lines[i]);
                    }
                }
            }

            return script.ToString();
        }
    }

    private static string Side(string? owner, int index) => owner is null ? $"Seite {index + 1}" : $"{owner} (Seite {index + 1})";

    private static IEnumerable<string> Lines(string? text) =>
        text is null ? [] : text.Split('\n').Select(line => line.TrimEnd('\r'));
}

/// <summary>
/// Builds the DDL proposal from a comparison (WP-20): the statements that make one side look like another. The SQL is
/// text for the user to copy; FerretSharp does not execute it (that may come with WP-22). Pure logic on the
/// comparison's payloads, no database access.
/// </summary>
/// <remarks>
/// Order: new tables, column changes, primary/unique keys, checks, foreign keys (after all tables exist), indexes;
/// hints and commented-out drops last. Within a phase by object name (ordinal), then in the order of the comparison's
/// rows (columns in column order). Names Oracle generated (<c>SYS_C…</c>) are not copied: constraints are added without
/// a name, so Oracle names them again; an index (which needs a name) gets <c>IX_&lt;TABLE&gt;_&lt;COLUMNS&gt;</c>.
/// </remarks>
public static class SchemaDdl
{
    /// <param name="rows">Object rows to cover; null = all differing objects.</param>
    public static DdlProposal Align(SchemaComparison comparison, int reference, int target, IReadOnlyList<CompareRow>? rows = null)
    {
        ArgumentNullException.ThrowIfNull(comparison);
        ArgumentOutOfRangeException.ThrowIfNegative(reference);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(reference, comparison.Sides.Count);
        ArgumentOutOfRangeException.ThrowIfNegative(target);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(target, comparison.Sides.Count);
        if (reference == target)
        {
            throw new ArgumentException("Reference and target must be different sides.", nameof(target));
        }

        var referenceOwner = comparison.Sides[reference].Owner;
        var targetOwner = comparison.Sides[target].Owner;
        var writer = new DdlWriter(reference, target, referenceOwner, targetOwner, comparison.Options);
        var keys = rows?.Select(r => r.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var row in comparison.Objects)
        {
            if (keys is null || keys.Contains(row.Key))
            {
                writer.Object(row);
            }
        }

        return new DdlProposal(reference, target, writer.Steps()) { ReferenceOwner = referenceOwner, TargetOwner = targetOwner };
    }
}
