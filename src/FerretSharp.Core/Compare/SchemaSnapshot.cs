using FerretSharp.Core.Schema;

namespace FerretSharp.Core.Compare;

/// <summary>
/// The structure of one schema as read for a comparison (WP-20): every table, view and materialized view with its
/// columns, constraints and indexes, read with a few dictionary queries for the whole schema. Comparing works only on
/// snapshots, so a snapshot could also come from a file later ("Prod before the release").
/// </summary>
/// <param name="Owner">The schema, exactly as in the dictionary (<c>ERP</c>).</param>
/// <param name="ReadAt">When the dictionary was read.</param>
/// <param name="Objects">Tables, views and materialized views of the schema, by name (ordinal).</param>
public sealed record SchemaSnapshot(string Owner, DateTimeOffset ReadAt, IReadOnlyList<ObjectSnapshot> Objects);

/// <summary>A table, view or materialized view of a <see cref="SchemaSnapshot"/>.</summary>
/// <param name="Name">Exact name as in the dictionary.</param>
/// <param name="Columns">Visible columns (<c>HIDDEN_COLUMN = 'NO'</c>) in column order, as <see cref="ISchemaReader.GetDetailsAsync"/> reads them.</param>
/// <param name="Constraints">
/// Primary key, unique, foreign key and check constraints as <see cref="ISchemaReader.GetConstraintsAsync"/> reads them,
/// without the generated NOT NULL checks (<see cref="ConstraintInfo.IsColumnNotNull"/>: they are <see cref="ColumnInfo.Nullable"/>).
/// Views: none.
/// </param>
/// <param name="Indexes">Indexes as <see cref="ISchemaReader.GetIndexesAsync"/> reads them (without LOB indexes). Views: none.</param>
public sealed record ObjectSnapshot(
    string Name,
    TableKind Kind,
    IReadOnlyList<ColumnInfo> Columns,
    IReadOnlyList<ConstraintInfo> Constraints,
    IReadOnlyList<IndexInfo> Indexes,
    bool IsIndexOrganized = false,
    bool Temporary = false,
    bool Partitioned = false);
