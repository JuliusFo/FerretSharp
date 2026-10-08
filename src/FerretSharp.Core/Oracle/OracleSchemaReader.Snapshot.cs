using FerretSharp.Core.Compare;
using FerretSharp.Core.Query;
using FerretSharp.Core.Schema;
using static FerretSharp.Core.Oracle.OracleReading;

namespace FerretSharp.Core.Oracle;

/// <summary>
/// Whole-schema reads: the snapshot of the schema comparison (WP-20) and all columns for the C# model comparison
/// (WP-27). The per-table statements of the other parts for every table of the owner at once, in a few round trips.
/// </summary>
public sealed partial class OracleSchemaReader
{
    // They also return rows of objects outside the snapshot, which are dropped by name: a dropped table leaves
    // ALL_TABLES, but its constraints stay in ALL_CONSTRAINTS under the BIN$… name (Oracle 23); IOT overflow segments
    // and nested tables are in ALL_TABLES. Constraint names are unique per owner (index names per index owner), so the
    // column lists are keyed by them as in the per-table statements.
    private const string SnapshotTablesSql = "SELECT table_name, iot_type, temporary, partitioned FROM all_tables WHERE owner = :owner";

    private const string SnapshotColumnsSql = $"""
        {ColumnsSelect}
         ORDER BY c.table_name, c.column_id
        """;

    private const string SnapshotConstraintsSql = $"""
        {ConstraintsSelect}
         ORDER BY c.table_name, {ConstraintOrder}
        """;

    private const string SnapshotConstraintColumnsSql = $"""
        {ConstraintColumnsSelect}
         ORDER BY constraint_name, position
        """;

    private const string SnapshotReferencedColumnsSql = $"""
        {ReferencedColumnsSelect}
         ORDER BY c.constraint_name, rc.position
        """;

    private const string SnapshotIndexesSql = $"""
        {IndexesSelect}
         ORDER BY table_name, index_name
        """;

    private const string SnapshotIndexColumnsSql = $"""
        {IndexColumnsSelect}
         ORDER BY index_owner, index_name, column_position
        """;

    private const string SnapshotIndexExpressionsSql = IndexExpressionsSelect;

    /// <summary>
    /// All columns of a schema (C# model comparison): <see cref="ColumnFields"/> in the same positions, without the LONG
    /// default (counts 32 KB a row in the fetch size) and the comments, which that comparison does not need.
    /// </summary>
    private static readonly string SchemaColumnsSql = $"""
        SELECT {ColumnFields.Replace("c.data_default", "NULL", StringComparison.Ordinal).Replace("cm.comments", "NULL", StringComparison.Ordinal)}
          FROM all_tab_cols c
         WHERE c.owner = :owner AND c.hidden_column = 'NO'
         ORDER BY c.table_name, c.column_id
        """;

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<ColumnInfo>>> GetColumnsAsync(string owner, CancellationToken cancellationToken) =>
        (await ReadColumnsByTableAsync(SchemaColumnsSql, [new("owner", owner)], many: true, cancellationToken))
            .ToDictionary(e => e.Key, e => (IReadOnlyList<ColumnInfo>)e.Value, StringComparer.Ordinal);

    public async Task<SchemaSnapshot> ReadSnapshotAsync(string owner, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var readAt = DateTimeOffset.Now;
        QueryParameter[] parameters = [new("owner", owner)];

        progress?.Report("Objekte");
        var objects = await GetTablesAsync(owner, cancellationToken);
        var storage = (await session.ReadListAsync(SnapshotTablesSql, parameters, reader =>
                (Name: reader.GetString(0), Iot: Text(reader, 1) == "IOT", Temporary: Text(reader, 2) == "Y", Partitioned: Text(reader, 3) == "YES"),
                cancellationToken, many: true))
            .ToDictionary(t => t.Name, t => (t.Iot, t.Temporary, t.Partitioned), StringComparer.Ordinal);

        progress?.Report("Spalten");
        var columns = await ReadColumnsByTableAsync(SnapshotColumnsSql, parameters, many: true, cancellationToken);

        progress?.Report("Constraints");
        var constraintColumns = await ReadNameListsAsync(SnapshotConstraintColumnsSql, parameters, cancellationToken, many: true);
        var referenced = await ReadNameListsAsync(SnapshotReferencedColumnsSql, parameters, cancellationToken, many: true);
        var constraints = (await session.ReadListAsync(SnapshotConstraintsSql, parameters, reader =>
                (Table: Text(reader, ConstraintsTableOrdinal), Constraint: ReadConstraint(reader, constraintColumns, referenced)),
                cancellationToken, many: true))
            .Where(c => c.Table is not null && !c.Constraint.IsColumnNotNull) // the NOT NULL checks are the columns' Nullable
            .GroupBy(c => c.Table!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(c => c.Constraint).ToList(), StringComparer.Ordinal);

        progress?.Report("Indizes");
        var indexColumns = await ReadIndexColumnsAsync(SnapshotIndexColumnsSql, SnapshotIndexExpressionsSql, parameters, many: true, cancellationToken);
        var indexes = (await session.ReadListAsync(SnapshotIndexesSql, parameters, reader =>
                (Table: reader.GetString(IndexesTableOrdinal), Index: ReadIndex(reader, indexColumns)), cancellationToken, many: true))
            .GroupBy(i => i.Table, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(i => i.Index).ToList(), StringComparer.Ordinal);

        var snapshots = objects
            .Select(o =>
            {
                var isView = o.Kind == TableKind.View;
                var (iot, temporary, partitioned) = isView ? default : storage.GetValueOrDefault(o.Name);
                return new ObjectSnapshot(
                    o.Name,
                    o.Kind,
                    columns.GetValueOrDefault(o.Name) ?? [],
                    isView ? [] : constraints.GetValueOrDefault(o.Name) ?? [],
                    isView ? [] : indexes.GetValueOrDefault(o.Name) ?? [],
                    IsIndexOrganized: o.Kind == TableKind.Table && iot, // as GetDetailsAsync
                    Temporary: temporary,
                    Partitioned: partitioned);
            })
            .ToList();
        return new SchemaSnapshot(owner, readAt, snapshots);
    }
}
