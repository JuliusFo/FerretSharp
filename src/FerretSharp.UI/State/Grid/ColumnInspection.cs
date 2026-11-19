using FerretSharp.Core.Data;
using FerretSharp.Core.Forms;
using FerretSharp.Core.Query;
using FerretSharp.Core.Schema;

namespace FerretSharp.UI.State;

/// <summary>What the column inspector reads once per table (WP-92): its indexes and the statistics of its columns.</summary>
public sealed record ColumnInspectorData(IReadOnlyList<IndexInfo> Indexes, IReadOnlyList<ColumnStatistics> Statistics);

/// <summary>
/// What the column inspector shows of one column (WP-92, ADR 0052), from what the tab has loaded: the column, its keys and
/// the relationships it is part of; indexes and statistics once <see cref="ColumnInspectorData"/> is read (null before).
/// </summary>
/// <param name="References">Relationships from this table whose columns include this one (declared, by convention, from the C# model).</param>
/// <param name="ReferencedBy">Relationships into this table whose target columns include this one.</param>
/// <param name="Indexes">The indexes with the column, those it leads first; null while not read, and for a view.</param>
/// <param name="Statistics">The column's statistics; null while not read, if Oracle has none, and for a view.</param>
public sealed record ColumnInspection(
    int Column,
    ColumnInfo Info,
    bool PrimaryKey,
    IReadOnlyList<ForeignKeyInfo> References,
    IReadOnlyList<ForeignKeyInfo> ReferencedBy,
    IReadOnlyList<IndexPosition>? Indexes,
    ColumnStatistics? Statistics)
{
    /// <summary>
    /// The object has indexes and statistics of its own: not a view – those of its tables count (review WP-92: every column of
    /// a view was warned about as having no index).
    /// </summary>
    public bool OwnIndexes { get; init; } = true;

    /// <summary>The indexes are read and none of them lets Oracle find rows by this column alone.</summary>
    public bool NoLeadingIndex => OwnIndexes && Indexes is { } indexes && !indexes.Any(i => i.Leads);

    public static ColumnInspection Of(
        TableDetails details, int column, IEnumerable<ForeignKeyInfo> outgoing, IEnumerable<ForeignKeyInfo> incoming, ColumnInspectorData? data)
    {
        ArgumentNullException.ThrowIfNull(details);
        var info = details.Columns[column];
        var name = info.Name;
        var own = details.Table.Kind != TableKind.View;
        return new ColumnInspection(
            column,
            info,
            details.PrimaryKey.Contains(name, StringComparer.Ordinal),
            outgoing.Where(fk => fk.FromColumns.Contains(name, StringComparer.Ordinal)).ToList(),
            incoming.Where(fk => fk.To == details.Table.Ref && fk.ToColumns.Contains(name, StringComparer.Ordinal)).ToList(),
            own && data is not null ? IndexAdvice.PositionsOf(name, data.Indexes) : null,
            own ? data?.Statistics.FirstOrDefault(s => s.Column == name) : null)
        {
            OwnIndexes = own,
        };
    }

    /// <summary>
    /// The filter row "Filter by this value" adds for the focused cell: <c>IS NULL</c>, or <c>=</c> with a text that parses
    /// back to exactly the value (<see cref="FkNavigation.FilterValue"/>, as the foreign key jumps). Null – no link – for a type
    /// that cannot be compared for equality (LOB, INTERVAL, floats, NUMBER beyond 28 digits, TIMESTAMP(9) with nanoseconds …)
    /// and for a pending value: the database does not have it yet (review WP-92: the shown text found nothing, or the filter
    /// was refused).
    /// </summary>
    public static FilterRow? FilterOf(FormField field)
    {
        ArgumentNullException.ThrowIfNull(field);
        if (field.Stage == ChangeStage.Pending)
        {
            return null;
        }

        if (field.Raw is not { } value)
        {
            return new FilterRow { Column = field.Info.Name, Op = FilterOperator.IsNull };
        }

        // TIMESTAMP(8/9) outside a key reads as DateTime with 7 digits: a text of it misses a value with nanoseconds
        // (second review WP-92; a key's PreciseTimestamp FilterValue checks itself).
        if (value is DateTime && ColumnCategories.LosesFractionsWhenRead(field.Info))
        {
            return null;
        }

        return FkNavigation.FilterValue(field.Info, value) is { } text
            ? new FilterRow { Column = field.Info.Name, Op = FilterOperator.Equals, Value = text }
            : null;
    }
}
