using FerretSharp.Core.Compare;
using FerretSharp.Core.Query;
using FerretSharp.Core.Resources;
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

    // As GetObjectInfoAsync: a materialized view's comment lives in ALL_MVIEW_COMMENTS and wins over its container table's.
    private const string SnapshotCommentsSql = """
        SELECT 'M', mview_name, comments FROM all_mview_comments WHERE owner = :owner AND comments IS NOT NULL
        UNION ALL
        SELECT 'T', table_name, comments FROM all_tab_comments WHERE owner = :owner AND comments IS NOT NULL
        """;

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

    // WP-82: view texts without LONG where they fit – TEXT_VC holds the first 4000 bytes, TEXT_LENGTH counts bytes. Only the
    // longer ones come as LONG (32 KB a row in the fetch size, OracleReading.FetchManyRows).
    private const string SnapshotViewTextsSql = "SELECT view_name, text_vc, text_length FROM all_views WHERE owner = :owner";

    private const string SnapshotLongViewTextsSql = "SELECT view_name, text FROM all_views WHERE owner = :owner AND text_length > 4000";

    // QUERY is LONG; materialized views are few.
    private const string SnapshotMViewsSql = """
        SELECT mview_name, query, refresh_method, refresh_mode, rewrite_enabled, build_mode
          FROM all_mviews WHERE owner = :owner
        """;

    // Views and materialized views selecting from others of the schema. A materialized view also depends on its container
    // table of the same name, which is left out.
    private const string SnapshotDependenciesSql = """
        SELECT DISTINCT name, referenced_name FROM all_dependencies
         WHERE owner = :owner AND type IN ('VIEW', 'MATERIALIZED VIEW')
           AND referenced_owner = :owner AND referenced_type IN ('VIEW', 'MATERIALIZED VIEW', 'TABLE')
           AND referenced_name <> name
        """;

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

        progress?.Report(OracleText.SnapshotStepObjects);
        var objects = await GetTablesAsync(owner, cancellationToken);
        var storage = (await session.ReadListAsync(SnapshotTablesSql, parameters, reader =>
                (Name: reader.GetString(0), Iot: Text(reader, 1) == "IOT", Temporary: Text(reader, 2) == "Y", Partitioned: Text(reader, 3) == "YES"),
                cancellationToken, many: true))
            .ToDictionary(t => t.Name, t => (t.Iot, t.Temporary, t.Partitioned), StringComparer.Ordinal);
        var comments = (await session.ReadListAsync(SnapshotCommentsSql, parameters, reader =>
                (MView: Text(reader, 0) == "M", Name: reader.GetString(1), Comment: reader.GetString(2)),
                cancellationToken, many: true))
            .ToLookup(c => c.Name, StringComparer.Ordinal);

        progress?.Report(OracleText.SnapshotStepColumns);
        var columns = await ReadColumnsByTableAsync(SnapshotColumnsSql, parameters, many: true, cancellationToken);

        progress?.Report(OracleText.SnapshotStepConstraints);
        var constraintColumns = await ReadNameListsAsync(SnapshotConstraintColumnsSql, parameters, cancellationToken, many: true);
        var referenced = await ReadNameListsAsync(SnapshotReferencedColumnsSql, parameters, cancellationToken, many: true);
        var constraints = (await session.ReadListAsync(SnapshotConstraintsSql, parameters, reader =>
                (Table: Text(reader, ConstraintsTableOrdinal), Constraint: ReadConstraint(reader, constraintColumns, referenced)),
                cancellationToken, many: true))
            .Where(c => c.Table is not null && !c.Constraint.IsColumnNotNull) // the NOT NULL checks are the columns' Nullable
            .GroupBy(c => c.Table!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(c => c.Constraint).ToList(), StringComparer.Ordinal);

        progress?.Report(OracleText.SnapshotStepIndexes);
        var indexColumns = await ReadIndexColumnsAsync(SnapshotIndexColumnsSql, SnapshotIndexExpressionsSql, parameters, many: true, cancellationToken);
        var indexes = (await session.ReadListAsync(SnapshotIndexesSql, parameters, reader =>
                (Table: reader.GetString(IndexesTableOrdinal), Index: ReadIndex(reader, indexColumns)), cancellationToken, many: true))
            .GroupBy(i => i.Table, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(i => i.Index).ToList(), StringComparer.Ordinal);

        progress?.Report(OracleText.SnapshotStepDefinitions);
        var views = objects.Where(o => o.Kind != TableKind.Table).Select(o => o.Name).ToHashSet(StringComparer.Ordinal);
        var definitions = views.Count == 0
            ? []
            : await ReadDefinitionsAsync(parameters, objects.Any(o => o.Kind == TableKind.MaterializedView), cancellationToken);
        var dependencies = views.Count == 0
            ? null
            : (await session.ReadListAsync(SnapshotDependenciesSql, parameters, reader => (Name: reader.GetString(0), On: reader.GetString(1)), cancellationToken, many: true))
                .Where(d => views.Contains(d.On))
                .ToLookup(d => d.Name, d => d.On, StringComparer.Ordinal);

        var snapshots = objects
            .Select(o =>
            {
                var isView = o.Kind == TableKind.View;
                var (iot, temporary, partitioned) = isView ? default : storage.GetValueOrDefault(o.Name);
                var definition = o.Kind == TableKind.Table ? default : definitions.GetValueOrDefault(o.Name);
                var dependsOn = dependencies is not null && dependencies.Contains(o.Name)
                    ? dependencies[o.Name].Order(StringComparer.Ordinal).ToList()
                    : null;
                return new ObjectSnapshot(
                    o.Name,
                    o.Kind,
                    columns.GetValueOrDefault(o.Name) ?? [],
                    isView ? ViewConstraints(o.Name) : constraints.GetValueOrDefault(o.Name) ?? [],
                    isView ? [] : indexes.GetValueOrDefault(o.Name) ?? [],
                    IsIndexOrganized: o.Kind == TableKind.Table && iot, // as GetDetailsAsync
                    Temporary: temporary,
                    Partitioned: partitioned,
                    Comment: (o.Kind == TableKind.MaterializedView ? Comment(o.Name, mview: true) : null) ?? Comment(o.Name, mview: false),
                    Definition: definition.Text,
                    DefinitionTruncated: definition.Truncated,
                    Invalid: o.IsInvalid,
                    MView: definition.MView,
                    DependsOn: dependsOn);
            })
            .ToList();
        return new SchemaSnapshot(owner, readAt, snapshots);

        string? Comment(string name, bool mview) => comments[name].FirstOrDefault(c => c.MView == mview).Comment;

        // A view's constraints say WITH READ ONLY or WITH CHECK OPTION, which are not in its text (WP-82).
        List<ConstraintInfo> ViewConstraints(string name) =>
            (constraints.GetValueOrDefault(name) ?? []).Where(c => c.Type is ConstraintType.ViewReadOnly or ConstraintType.ViewCheckOption).ToList();
    }

    /// <summary>
    /// The query of every view and materialized view as <see cref="GetDetailsAsync"/> reads it one by one (trimmed, cut
    /// at the LONG fetch limit), and the options of the materialized views.
    /// </summary>
    private async Task<Dictionary<string, (string? Text, bool Truncated, MViewOptions? MView)>> ReadDefinitionsAsync(
        IReadOnlyList<QueryParameter> parameters, bool mviews, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, (string? Text, bool Truncated, MViewOptions? MView)>(StringComparer.Ordinal);
        var views = await session.ReadListAsync(SnapshotViewTextsSql, parameters, reader =>
            (Name: reader.GetString(0), Text: Text(reader, 1), Long: Long(reader, 2) > 4000), cancellationToken, many: true);
        foreach (var view in views)
        {
            result[view.Name] = (view.Text?.Trim(), false, null);
        }

        if (views.Any(v => v.Long))
        {
            foreach (var (name, text) in await session.ReadListAsync(SnapshotLongViewTextsSql, parameters, reader =>
                         (reader.GetString(0), Text(reader, 1)), cancellationToken, many: true))
            {
                result[name] = (text?.Trim(), text is not null && text.Length >= OracleSession.LongFetchSize, null);
            }
        }

        if (mviews)
        {
            foreach (var (name, query, options) in await session.ReadListAsync(SnapshotMViewsSql, parameters, reader =>
                         (reader.GetString(0), Text(reader, 1),
                          new MViewOptions(Text(reader, 2) ?? "FORCE", Text(reader, 3) ?? "DEMAND", Text(reader, 4) == "Y", Text(reader, 5) == "PREBUILT")),
                         cancellationToken, many: true))
            {
                result[name] = (query?.Trim(), query is not null && query.Length >= OracleSession.LongFetchSize, options);
            }
        }

        return result;
    }
}
