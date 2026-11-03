using FerretSharp.Core.Schema;

namespace FerretSharp.UI.State;

/// <summary>Whether a relationship of the C# model can become a foreign key constraint ("Create constraint" in the "Constraints" view).</summary>
public enum ModelConstraintOffer
{
    /// <summary>Both sides are tables, the referenced columns are the primary key.</summary>
    Ready,

    /// <summary>
    /// Both sides are tables, but the referenced columns are not the primary key: a unique key may still fit – the designer
    /// says so once it has loaded the table (ORA-02270).
    /// </summary>
    NotPrimaryKey,

    /// <summary>A side is a view, a table reached through a synonym or not loaded: the designer cannot add it.</summary>
    NotTables,
}

public static class ModelConstraintOffers
{
    /// <summary>
    /// What "Create constraint" does for <paramref name="relation"/>: both sides must be tables of the schema the designer
    /// offers to reference (no view, no synonym). The primary key is known without a round trip, unique keys are not.
    /// </summary>
    public static ModelConstraintOffer Of(ForeignKeyInfo relation, Func<TableRef, TableSummary?> find, Func<TableRef, IReadOnlyList<string>> primaryKeyOf)
    {
        ArgumentNullException.ThrowIfNull(relation);
        static bool IsTable(TableSummary? table) => table is { Kind: TableKind.Table, Synonym: null };
        if (!IsTable(find(relation.From)) || !IsTable(find(relation.To)))
        {
            return ModelConstraintOffer.NotTables;
        }

        var key = primaryKeyOf(relation.To);
        return key.Count == relation.ToColumns.Count && key.ToHashSet(StringComparer.Ordinal).SetEquals(relation.ToColumns)
            ? ModelConstraintOffer.Ready
            : ModelConstraintOffer.NotPrimaryKey;
    }
}
