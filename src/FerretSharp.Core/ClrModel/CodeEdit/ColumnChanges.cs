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

/// <summary>The changes of a designer draft that matter for the C# project (WP-41), in the draft's order.</summary>
public static class ColumnChanges
{
    /// <param name="table">The table as it was before the draft.</param>
    public static IReadOnlyList<ColumnChange> Of(TableDetails table, TableDraft draft)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(draft);
        var changes = new List<ColumnChange>();
        foreach (var column in draft.Columns)
        {
            if (column.IsNew)
            {
                if (!column.Dropped)
                {
                    changes.Add(new ColumnChange(ColumnChangeKind.Added, column.Column.Name, null, column.Column));
                }

                continue;
            }

            var at = table.IndexOf(column.Original!);
            if (at < 0)
            {
                continue;
            }

            var before = table.Columns[at];
            if (column.Dropped)
            {
                changes.Add(new ColumnChange(ColumnChangeKind.Dropped, before.Name, before, null));
                continue;
            }

            var after = column.Column;
            if (after.Name != before.Name)
            {
                changes.Add(new ColumnChange(ColumnChangeKind.Renamed, before.Name, before, after));
            }

            if (after.DataType != before.DataType)
            {
                changes.Add(new ColumnChange(ColumnChangeKind.TypeChanged, before.Name, before, after));
            }
            else if (FacetsDiffer(before, after))
            {
                changes.Add(new ColumnChange(ColumnChangeKind.Facets, before.Name, before, after));
            }
        }

        return changes;
    }

    /// <summary>NULL, precision, scale or – for a type with a length – length and its semantics differ.</summary>
    internal static bool FacetsDiffer(ColumnInfo before, ColumnInfo after) =>
        before.Nullable != after.Nullable || before.Precision != after.Precision || before.Scale != after.Scale
        || (OracleTypes.HasLength(after.DataType) && (before.Length != after.Length || before.CharSemantics != after.CharSemantics));
}
