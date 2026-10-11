using FerretSharp.Core.Ddl;
using FerretSharp.Core.Resources;

namespace FerretSharp.Core.Compare;

/// <summary>The DDL that makes <see cref="Target"/> look like <see cref="Reference"/> (indexes into the comparison's sides).</summary>
/// <param name="Steps">Runnable from the SQL editor since WP-22; steps that are not executable stay comments there.</param>
public sealed record DdlProposal(int Reference, int Target, IReadOnlyList<DdlStep> Steps)
{
    /// <summary>Owner of the reference side, for the script header; null = only the side number is known.</summary>
    public string? ReferenceOwner { get; init; }

    /// <summary>Owner of the target side, for the script header; null = only the side number is known.</summary>
    public string? TargetOwner { get; init; }

    /// <summary>
    /// The steps as a script under a header (<see cref="DdlScript.Render"/>), ending with a line break; lines end as usual
    /// on this platform.
    /// </summary>
    public string Script
    {
        get
        {
            List<string> header =
            [
                "-- " + TextFormat.Format(CompareText.ScriptHeader, Side(TargetOwner, Target), Side(ReferenceOwner, Reference)),
                "-- " + CompareText.ScriptGenerated,
            ];
            if (Steps.Count == 0)
            {
                header.Add("-- " + CompareText.ScriptNoDifferences);
            }

            return (DdlScript.Render(Steps, header) + "\n").ReplaceLineEndings();
        }
    }

    private static string Side(string? owner, int index) =>
        owner is null ? TextFormat.Format(CompareText.Side, index + 1) : TextFormat.Format(CompareText.SideWithOwner, owner, index + 1);
}

/// <summary>
/// Builds the DDL proposal from a comparison (WP-20): the statements that make one side look like another. The SQL is
/// text to copy, save or open in the SQL editor, which runs it through the schema path (WP-22). Pure logic on the
/// comparison's payloads, no database access.
/// </summary>
/// <remarks>
/// Order as in the designer (<see cref="DdlScript.Stage"/>, R4b): constraints to recreate dropped first, new tables,
/// column changes, new columns, primary/unique keys, checks, foreign keys (after all tables exist), indexes, comments;
/// hints and commented-out drops last. Within a stage by object name (ordinal), then in the order of the comparison's
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
