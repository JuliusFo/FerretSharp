using System.Globalization;
using FerretSharp.Core.Query;
using FerretSharp.Core.Schema;
using static FerretSharp.Core.Oracle.OracleReading;

namespace FerretSharp.Core.Oracle;

/// <summary>The details views of one object (v1.7): info, constraints, indexes, dependencies, DDL.</summary>
public sealed partial class OracleSchemaReader
{
    // A materialized view is TABLE and MATERIALIZED VIEW in ALL_OBJECTS; its container table carries the statistics.
    // Its comment lives in ALL_MVIEW_COMMENTS.
    private const string ObjectInfoSql = """
        SELECT o.status, o.created, o.last_ddl_time,
               (SELECT c.comments FROM all_tab_comments c
                 WHERE c.owner = o.owner AND c.table_name = o.object_name AND ROWNUM = 1) AS table_comment,
               (SELECT c.comments FROM all_mview_comments c
                 WHERE c.owner = o.owner AND c.mview_name = o.object_name AND ROWNUM = 1) AS mview_comment,
               t.num_rows, t.last_analyzed, t.tablespace_name, t.partitioned, t.temporary
          FROM all_objects o
          LEFT JOIN all_tables t ON t.owner = o.owner AND t.table_name = o.object_name
         WHERE o.owner = :owner AND o.object_name = :name AND o.object_type = :object_type
        """;

    private const string ConstraintsSql = $"""
        {ConstraintsSelect}
           AND c.table_name = :name
         ORDER BY {ConstraintOrder}
        """;

    private const string ConstraintColumnsSql = $"""
        {ConstraintColumnsSelect} AND table_name = :name
         ORDER BY constraint_name, position
        """;

    private const string ReferencedColumnsSql = $"""
        {ReferencedColumnsSelect}
           AND c.table_name = :name
         ORDER BY c.constraint_name, rc.position
        """;

    private const string IndexesSql = $"""
        {IndexesSelect}
           AND table_name = :name
         ORDER BY index_name
        """;

    private const string IndexColumnsSql = $"""
        {IndexColumnsSelect}
           AND table_name = :name
         ORDER BY index_owner, index_name, column_position
        """;

    private const string IndexExpressionsSql = $"""
        {IndexExpressionsSelect}
           AND table_name = :name
        """;

    // STANDARD/DBMS_STANDARD are referenced by everything and say nothing.
    private const string UsesSql = """
        SELECT DISTINCT d.referenced_owner, d.referenced_name, d.referenced_type, o.status
          FROM all_dependencies d
          LEFT JOIN all_objects o
            ON o.owner = d.referenced_owner AND o.object_name = d.referenced_name AND o.object_type = d.referenced_type
         WHERE d.owner = :owner AND d.name = :name AND d.type IN ('TABLE', 'VIEW', 'MATERIALIZED VIEW')
           AND d.referenced_link_name IS NULL
           AND d.referenced_type <> 'NON-EXISTENT'
           AND NOT (d.referenced_owner = 'SYS' AND d.referenced_name IN ('STANDARD', 'DBMS_STANDARD'))
         ORDER BY d.referenced_type, d.referenced_owner, d.referenced_name
        """;

    private const string UsedBySql = """
        SELECT DISTINCT d.owner, d.name, d.type, o.status
          FROM all_dependencies d
          LEFT JOIN all_objects o ON o.owner = d.owner AND o.object_name = d.name AND o.object_type = d.type
         WHERE d.referenced_owner = :owner AND d.referenced_name = :name
           AND d.referenced_type IN ('TABLE', 'VIEW', 'MATERIALIZED VIEW')
         ORDER BY d.type, d.owner, d.name
        """;

    // A function call in a plain query: DBMS_METADATA only reads. Object type names use underscores here.
    private const string DdlSql = "SELECT DBMS_METADATA.GET_DDL(:object_type, :name, :owner) FROM DUAL";

    public Task<ObjectInfo> GetObjectInfoAsync(TableSummary table, CancellationToken cancellationToken) =>
        session.ExecuteReaderAsync(ObjectInfoSql, [new("owner", table.Owner), new("name", table.Name), new("object_type", ObjectType(table.Kind))], async (reader, ct) =>
        {
            if (!await reader.ReadAsync(ct))
            {
                return new ObjectInfo("N/A", null, null, null, null, null, null, false, false);
            }

            return new ObjectInfo(
                Status: reader.GetString(0),
                Created: Date(reader, 1),
                LastDdl: Date(reader, 2),
                Comment: (table.Kind == TableKind.MaterializedView ? Text(reader, 4) : null) ?? Text(reader, 3),
                NumRows: reader.IsDBNull(5) ? null : Convert.ToInt64(reader.GetValue(5), CultureInfo.InvariantCulture),
                LastAnalyzed: Date(reader, 6),
                Tablespace: Text(reader, 7),
                Partitioned: Text(reader, 8) == "YES",
                Temporary: Text(reader, 9) == "Y");
        }, cancellationToken);

    public async Task<IReadOnlyList<ConstraintInfo>> GetConstraintsAsync(TableRef table, CancellationToken cancellationToken)
    {
        QueryParameter[] parameters = [new("owner", table.Owner), new("name", table.Name)];
        var columns = await ReadNameListsAsync(ConstraintColumnsSql, parameters, cancellationToken);
        var referenced = await ReadNameListsAsync(ReferencedColumnsSql, parameters, cancellationToken);
        return await session.ReadListAsync(ConstraintsSql, parameters, reader => ReadConstraint(reader, columns, referenced), cancellationToken);
    }

    public async Task<IReadOnlyList<IndexInfo>> GetIndexesAsync(TableRef table, CancellationToken cancellationToken)
    {
        QueryParameter[] parameters = [new("owner", table.Owner), new("name", table.Name)];
        var columnsByIndex = await ReadIndexColumnsAsync(IndexColumnsSql, IndexExpressionsSql, parameters, many: false, cancellationToken);
        return await session.ReadListAsync(IndexesSql, parameters, reader => ReadIndex(reader, columnsByIndex), cancellationToken);
    }

    public async Task<ObjectDependencies> GetDependenciesAsync(TableSummary table, CancellationToken cancellationToken)
    {
        QueryParameter[] parameters = [new("owner", table.Owner), new("name", table.Name)];
        var uses = await ReadDependenciesAsync(UsesSql, parameters, cancellationToken);
        var usedBy = await ReadDependenciesAsync(UsedBySql, parameters, cancellationToken);
        return new ObjectDependencies(uses, usedBy);
    }

    public Task<string> GetDdlAsync(TableSummary table, CancellationToken cancellationToken)
    {
        var objectType = ObjectType(table.Kind).Replace(' ', '_');
        return session.ExecuteReaderAsync(DdlSql, [new("object_type", objectType), new("name", table.Name), new("owner", table.Owner)], async (reader, ct) =>
            await reader.ReadAsync(ct) && !reader.IsDBNull(0) ? reader.GetString(0).Trim() : "", cancellationToken);
    }

    private async Task<IReadOnlyList<DependencyInfo>> ReadDependenciesAsync(
        string sql, IReadOnlyList<QueryParameter> parameters, CancellationToken cancellationToken) =>
        await session.ReadListAsync(sql, parameters, reader =>
            new DependencyInfo(reader.GetString(0), reader.GetString(1), reader.GetString(2), Text(reader, 3)), cancellationToken);
}
