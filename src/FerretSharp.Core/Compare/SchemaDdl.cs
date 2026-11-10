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

    /// <summary>How many objects the user chose in the matrix; null = all differing objects.</summary>
    public int? Selected { get; init; }

    /// <summary>Chosen objects that are alike on these two sides (they differ only on others): no step for them.</summary>
    public IReadOnlyList<string> Unchanged { get; init; } = [];

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
            if (Selected is { } selected)
            {
                header.Add("-- " + TextFormat.Plural(selected, CompareText.ScriptSelectedOne, CompareText.ScriptSelectedOther));
            }

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
    /// <param name="rows">
    /// Object rows to cover, as the user chose them in the matrix; null = all differing objects. A foreign key to a table
    /// the target lacks and the choice leaves out gets a warning: it would fail (ORA-00942) unless that table comes first.
    /// </param>
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
        var keys = rows?.Select(r => r.Key).ToHashSet(StringComparer.Ordinal);
        var chosen = comparison.Objects.Where(row => keys is null || keys.Contains(row.Key)).ToList();
        var notCreated = keys is null
            ? []
            : comparison.Objects
                .Where(row => !keys.Contains(row.Key) && row.Cells[target].State == CellState.Missing && row.Cells[reference].Object is not null)
                .Select(row => row.Cells[reference].Object!.Name)
                .ToHashSet(StringComparer.Ordinal);

        var writer = new DdlWriter(reference, target, referenceOwner, targetOwner, comparison.Options, notCreated, ViewRanks(comparison.Sides[reference]));
        var unchanged = new List<string>();
        foreach (var row in chosen)
        {
            var before = writer.Count;
            writer.Object(row);
            if (writer.Count == before)
            {
                unchanged.Add(row.Name);
            }
        }

        return new DdlProposal(reference, target, writer.Steps())
        {
            ReferenceOwner = referenceOwner,
            TargetOwner = targetOwner,
            Selected = keys is null ? null : chosen.Count,
            Unchanged = keys is null ? [] : unchanged,
        };
    }

    /// <summary>
    /// Per view and materialized view of <paramref name="side"/>: 0 if it selects from no other of them, else one more than
    /// the deepest it selects from (WP-82). A cycle, which Oracle does not allow, ends at 0.
    /// </summary>
    internal static Dictionary<string, int> ViewRanks(SchemaSnapshot side)
    {
        var dependsOn = side.Objects.Where(o => o.DependsOn is { Count: > 0 }).ToDictionary(o => o.Name, o => o.DependsOn!, StringComparer.Ordinal);
        var ranks = new Dictionary<string, int>(StringComparer.Ordinal);
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in dependsOn.Keys)
        {
            Rank(name);
        }

        return ranks;

        int Rank(string name)
        {
            if (ranks.TryGetValue(name, out var known))
            {
                return known;
            }

            if (!dependsOn.TryGetValue(name, out var on) || !visiting.Add(name))
            {
                return 0;
            }

            var rank = on.Max(n => Rank(n) + 1);
            visiting.Remove(name);
            ranks[name] = rank;
            return rank;
        }
    }
}
