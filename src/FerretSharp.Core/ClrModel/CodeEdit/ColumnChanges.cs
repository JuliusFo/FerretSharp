using FerretSharp.Core.Design;
using FerretSharp.Core.Schema;

namespace FerretSharp.Core.ClrModel;

/// <summary>What happened to a column, as far as the C# project is concerned (WP-41).</summary>
public enum ColumnChangeKind
{
    /// <summary>A new column: a property and, if it says more than the name, a configuration line.</summary>
    Added,

    /// <summary>Length, precision, scale, character semantics or NULL changed: what the project configures is adjusted.</summary>
    Facets,

    /// <summary>The column is gone: the property and its configuration lines go too.</summary>
    Dropped,

    /// <summary>Renamed – not changed in the code yet (decision of the user, first stage), only named.</summary>
    Renamed,

    /// <summary>Another type – not changed in the code (the C# type would change), only named.</summary>
    TypeChanged,
}

/// <param name="Column">Its name before; for a new column its name.</param>
/// <param name="Before">The column as it was; null for a new one.</param>
/// <param name="After">The column as it is now; null for a dropped one.</param>
public sealed record ColumnChange(ColumnChangeKind Kind, string Column, ColumnInfo? Before, ColumnInfo? After);

/// <summary>The changes between two states of a table that matter for the C# project (WP-41).</summary>
public static class ColumnChanges
{
    /// <summary>The changes a designer draft makes, before it runs (the preview).</summary>
    /// <param name="table">The table as it was before the draft.</param>
    public static IReadOnlyList<ColumnChange> Of(TableDetails table, TableDraft draft)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(draft);
        return Of(table, After(draft), RenamesOf(draft));
    }

    /// <summary>The columns a draft leaves, in its order.</summary>
    public static IReadOnlyList<ColumnInfo> After(TableDraft draft) => [.. draft.Columns.Where(c => !c.Dropped).Select(c => c.Column)];

    /// <summary>The columns a draft renames: old name → new name.</summary>
    public static IReadOnlyDictionary<string, string> RenamesOf(TableDraft draft) =>
        draft.Columns.Where(c => c is { Original: not null, Dropped: false } && c.Original != c.Column.Name)
            .ToDictionary(c => c.Original!, c => c.Column.Name, StringComparer.Ordinal);

    /// <summary>
    /// The changes between the table as it was and its columns now – after a run that may have stopped halfway, or several
    /// runs (review WP-41): a rename counts if the new name is there, else the column is looked for under its old name.
    /// Columns before in their order, then the new ones in theirs.
    /// </summary>
    /// <param name="renames">Old name → new name, as the drafts meant them.</param>
    public static IReadOnlyList<ColumnChange> Of(TableDetails before, IReadOnlyList<ColumnInfo> after, IReadOnlyDictionary<string, string> renames)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        ArgumentNullException.ThrowIfNull(renames);
        var now = after.GroupBy(c => c.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var matched = new HashSet<string>(StringComparer.Ordinal);
        var changes = new List<ColumnChange>();
        foreach (var column in before.Columns)
        {
            var current = renames.TryGetValue(column.Name, out var renamed) && now.TryGetValue(renamed, out var moved) ? moved
                : now.GetValueOrDefault(column.Name);
            if (current is null)
            {
                changes.Add(new ColumnChange(ColumnChangeKind.Dropped, column.Name, column, null));
                continue;
            }

            matched.Add(current.Name);
            if (current.Name != column.Name)
            {
                changes.Add(new ColumnChange(ColumnChangeKind.Renamed, column.Name, column, current));
            }

            if (current.DataType != column.DataType)
            {
                changes.Add(new ColumnChange(ColumnChangeKind.TypeChanged, column.Name, column, current));
            }
            else if (FacetsDiffer(column, current))
            {
                changes.Add(new ColumnChange(ColumnChangeKind.Facets, column.Name, column, current));
            }
        }

        changes.AddRange(after.Where(c => !matched.Contains(c.Name) && c.Name.Trim().Length > 0)
            .Select(c => new ColumnChange(ColumnChangeKind.Added, c.Name, null, c)));
        return changes;
    }

    /// <summary>NULL, precision, scale or – for a type with a length – length and its semantics differ.</summary>
    internal static bool FacetsDiffer(ColumnInfo before, ColumnInfo after) =>
        before.Nullable != after.Nullable || before.Precision != after.Precision || before.Scale != after.Scale
        || (OracleTypes.HasLength(after.DataType) && (before.Length != after.Length || before.CharSemantics != after.CharSemantics));
}
