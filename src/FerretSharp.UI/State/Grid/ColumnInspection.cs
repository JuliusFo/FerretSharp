using FerretSharp.Core.Schema;

namespace FerretSharp.UI.State;

/// <summary>What the column inspector reads once per table (WP-92): its indexes and the statistics of its columns.</summary>
public sealed record ColumnInspectorData(IReadOnlyList<IndexInfo> Indexes, IReadOnlyList<ColumnStatistics> Statistics, DateTimeOffset ReadAt);

/// <summary>
/// What the column inspector shows of one column (WP-92, ADR 0052), from what the tab has loaded: the column, its keys and
/// the relationships it is part of; indexes and statistics once <see cref="ColumnInspectorData"/> is read (null before).
/// </summary>
/// <param name="References">Relationships from this table whose columns include this one (declared, by convention, from the C# model).</param>
/// <param name="ReferencedBy">Relationships into this table whose target columns include this one.</param>
/// <param name="Indexes">The indexes with the column, those it leads first; null while not read.</param>
/// <param name="Statistics">The column's statistics; null while not read or if Oracle has none.</param>
public sealed record ColumnInspection(
    int Column,
    ColumnInfo Info,
    bool PrimaryKey,
    IReadOnlyList<ForeignKeyInfo> References,
    IReadOnlyList<ForeignKeyInfo> ReferencedBy,
    IReadOnlyList<IndexPosition>? Indexes,
    ColumnStatistics? Statistics)
{
    /// <summary>The indexes are read and none of them lets Oracle find rows by this column alone.</summary>
    public bool NoLeadingIndex => Indexes is { } indexes && !indexes.Any(i => i.Leads);

    public static ColumnInspection Of(
        TableDetails details, int column, IEnumerable<ForeignKeyInfo> outgoing, IEnumerable<ForeignKeyInfo> incoming, ColumnInspectorData? data)
    {
        ArgumentNullException.ThrowIfNull(details);
        var info = details.Columns[column];
        var name = info.Name;
        return new ColumnInspection(
            column,
            info,
            details.PrimaryKey.Contains(name, StringComparer.Ordinal),
            outgoing.Where(fk => fk.FromColumns.Contains(name, StringComparer.Ordinal)).ToList(),
            incoming.Where(fk => fk.To == details.Table.Ref && fk.ToColumns.Contains(name, StringComparer.Ordinal)).ToList(),
            data is null ? null : IndexAdvice.PositionsOf(name, data.Indexes),
            data?.Statistics.FirstOrDefault(s => s.Column == name));
    }
}
