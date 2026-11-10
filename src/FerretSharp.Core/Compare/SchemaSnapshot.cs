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
/// Views: only <c>WITH READ ONLY</c> and <c>WITH CHECK OPTION</c> (<see cref="ConstraintType.ViewReadOnly"/>,
/// <see cref="ConstraintType.ViewCheckOption"/>; WP-82), which are not in the view's text.
/// </param>
/// <param name="Indexes">Indexes as <see cref="ISchemaReader.GetIndexesAsync"/> reads them (without LOB indexes). Views: none.</param>
/// <param name="Comment">
/// The object's comment as <see cref="ISchemaReader.GetObjectInfoAsync"/> reads it (a materialized view's from
/// <c>ALL_MVIEW_COMMENTS</c>); null for none. Not compared – the DDL proposal writes it for an object it creates.
/// </param>
/// <param name="Definition">
/// Views and materialized views: the query as Oracle keeps it (<c>ALL_VIEWS.TEXT</c>, <c>ALL_MVIEWS.QUERY</c>), trimmed
/// as <see cref="ISchemaReader.GetDetailsAsync"/> reads it; null for tables (WP-82).
/// </param>
/// <param name="DefinitionTruncated">The definition reached the LONG fetch limit (32 767 characters) and may be cut off.</param>
/// <param name="Invalid"><c>ALL_OBJECTS.STATUS = 'INVALID'</c> (<see cref="TableSummary.IsInvalid"/>): a view's columns then have the type <c>UNDEFINED</c>.</param>
/// <param name="MView">Refresh and build options of a materialized view; null for tables and views.</param>
/// <param name="DependsOn">
/// Views and materialized views of the same schema this view or materialized view selects from (<c>ALL_DEPENDENCIES</c>),
/// for the order of a script; null for none.
/// </param>
public sealed record ObjectSnapshot(
    string Name,
    TableKind Kind,
    IReadOnlyList<ColumnInfo> Columns,
    IReadOnlyList<ConstraintInfo> Constraints,
    IReadOnlyList<IndexInfo> Indexes,
    bool IsIndexOrganized = false,
    bool Temporary = false,
    bool Partitioned = false,
    string? Comment = null,
    string? Definition = null,
    bool DefinitionTruncated = false,
    bool Invalid = false,
    MViewOptions? MView = null,
    IReadOnlyList<string>? DependsOn = null)
{
    /// <summary><c>WITH READ ONLY</c> or <c>WITH CHECK OPTION</c> of a view; <see cref="ViewOption.None"/> otherwise.</summary>
    public ViewOption ViewOption =>
        Kind != TableKind.View ? ViewOption.None
        : Constraints.Any(c => c.Type == ConstraintType.ViewReadOnly) ? ViewOption.ReadOnly
        : Constraints.Any(c => c.Type == ConstraintType.ViewCheckOption) ? ViewOption.CheckOption
        : ViewOption.None;
}
