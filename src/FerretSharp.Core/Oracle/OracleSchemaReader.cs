using System.Data.Common;
using System.Globalization;
using FerretSharp.Core.Query;
using FerretSharp.Core.Schema;

namespace FerretSharp.Core.Oracle;

/// <summary>Reads schema metadata from the ALL_* views. Every query is restricted to one owner.</summary>
public sealed class OracleSchemaReader(OracleSession session) : ISchemaReader
{
    // Nested tables, secondary (domain index) tables, IOT overflow segments, recycle bin entries and
    // the container tables of materialized views are not interesting objects for browsing.
    private const string TablesSql = """
        SELECT object_name, object_kind FROM (
            SELECT t.table_name AS object_name, 'TABLE' AS object_kind
              FROM all_tables t
             WHERE t.owner = :owner
               AND t.nested = 'NO'
               AND t.secondary = 'N'
               AND t.dropped = 'NO'
               AND (t.iot_type IS NULL OR t.iot_type = 'IOT')
               AND NOT EXISTS (SELECT 1 FROM all_mviews m WHERE m.owner = t.owner AND m.mview_name = t.table_name)
            UNION ALL
            SELECT v.view_name, 'VIEW' FROM all_views v WHERE v.owner = :owner
            UNION ALL
            SELECT m.mview_name, 'MVIEW' FROM all_mviews m WHERE m.owner = :owner)
        """;

    private const string ForeignKeysSql = """
        SELECT c.constraint_name, c.table_name, cc.column_name, r.owner, r.table_name, rc.column_name
          FROM all_constraints c
          JOIN all_cons_columns cc
            ON cc.owner = c.owner AND cc.constraint_name = c.constraint_name AND cc.table_name = c.table_name
          JOIN all_constraints r
            ON r.owner = c.r_owner AND r.constraint_name = c.r_constraint_name
          JOIN all_cons_columns rc
            ON rc.owner = r.owner AND rc.constraint_name = r.constraint_name AND rc.position = cc.position
         WHERE c.owner = :owner
           AND c.constraint_type = 'R'
         ORDER BY c.constraint_name, cc.position
        """;

    private const string ColumnsSql = """
        SELECT column_name, data_type, char_used, char_length, data_length, data_precision, data_scale,
               nullable, identity_column, data_default, column_id
          FROM all_tab_columns
         WHERE owner = :owner AND table_name = :name
         ORDER BY column_id
        """;

    private const string KeysSql = """
        SELECT c.constraint_type, c.constraint_name, cc.column_name
          FROM all_constraints c
          JOIN all_cons_columns cc
            ON cc.owner = c.owner AND cc.constraint_name = c.constraint_name AND cc.table_name = c.table_name
         WHERE c.owner = :owner AND c.table_name = :name AND c.constraint_type IN ('P', 'U')
         ORDER BY c.constraint_type, c.constraint_name, cc.position
        """;

    private const string IotSql = "SELECT iot_type FROM all_tables WHERE owner = :owner AND table_name = :name";

    public Task<IReadOnlyList<TableSummary>> GetTablesAsync(string owner, CancellationToken cancellationToken) =>
        session.ExecuteReaderAsync(TablesSql, [new("owner", owner)], async (reader, ct) =>
        {
            var tables = new List<TableSummary>();
            while (await reader.ReadAsync(ct))
            {
                var kind = reader.GetString(1) switch
                {
                    "VIEW" => TableKind.View,
                    "MVIEW" => TableKind.MaterializedView,
                    _ => TableKind.Table,
                };
                tables.Add(new TableSummary(owner, reader.GetString(0), kind));
            }

            tables.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
            return (IReadOnlyList<TableSummary>)tables;
        }, cancellationToken);

    public Task<IReadOnlyList<ForeignKeyInfo>> GetForeignKeysAsync(string owner, CancellationToken cancellationToken) =>
        session.ExecuteReaderAsync(ForeignKeysSql, [new("owner", owner)], async (reader, ct) =>
        {
            var rows = new List<(string Name, string Table, string Column, string RefOwner, string RefTable, string RefColumn)>();
            while (await reader.ReadAsync(ct))
            {
                rows.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5)));
            }

            return (IReadOnlyList<ForeignKeyInfo>)rows
                .GroupBy(r => r.Name)
                .Select(g =>
                {
                    var first = g.First();
                    return new ForeignKeyInfo(
                        g.Key,
                        new TableRef(owner, first.Table),
                        g.Select(r => r.Column).ToList(),
                        new TableRef(first.RefOwner, first.RefTable),
                        g.Select(r => r.RefColumn).ToList(),
                        FkSource.Declared);
                })
                .ToList();
        }, cancellationToken);

    public async Task<TableDetails> GetDetailsAsync(TableSummary table, CancellationToken cancellationToken)
    {
        QueryParameter[] parameters = [new("owner", table.Owner), new("name", table.Name)];

        var columns = await session.ExecuteReaderAsync(ColumnsSql, parameters, ReadColumnsAsync, cancellationToken);

        var keys = await session.ExecuteReaderAsync(KeysSql, parameters, async (reader, ct) =>
        {
            var result = new List<(string Type, string Name, string Column)>();
            while (await reader.ReadAsync(ct))
            {
                result.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
            }

            return result;
        }, cancellationToken);

        var isIot = table.Kind == TableKind.Table && await session.ExecuteReaderAsync(IotSql, parameters, async (reader, ct) =>
            await reader.ReadAsync(ct) && !reader.IsDBNull(0) && reader.GetString(0) == "IOT", cancellationToken);

        var primaryKey = keys.Where(k => k.Type == "P").Select(k => k.Column).ToList();
        var uniqueKeys = keys
            .Where(k => k.Type == "U")
            .GroupBy(k => k.Name)
            .Select(g => (IReadOnlyList<string>)g.Select(k => k.Column).ToList())
            .ToList();

        return new TableDetails(table, columns, primaryKey, uniqueKeys, isIot);
    }

    private static async Task<IReadOnlyList<ColumnInfo>> ReadColumnsAsync(DbDataReader reader, CancellationToken ct)
    {
        var columns = new List<ColumnInfo>();
        while (await reader.ReadAsync(ct))
        {
            var dataType = reader.GetString(1);
            var charSemantics = !reader.IsDBNull(2) && reader.GetString(2) == "C";
            var charLength = GetInt(reader, 3);
            var dataLength = GetInt(reader, 4);
            int? length = dataType switch
            {
                "VARCHAR2" or "CHAR" => charSemantics ? charLength : dataLength,
                "NVARCHAR2" or "NCHAR" => charLength,
                "RAW" => dataLength,
                _ => null,
            };

            columns.Add(new ColumnInfo(
                Name: reader.GetString(0),
                DataType: dataType,
                Length: length,
                CharSemantics: charSemantics,
                Precision: GetInt(reader, 5),
                Scale: GetInt(reader, 6),
                Nullable: reader.GetString(7) == "Y",
                IsIdentity: !reader.IsDBNull(8) && reader.GetString(8) == "YES",
                Default: reader.IsDBNull(9) ? null : reader.GetString(9).Trim() is { Length: > 0 } d ? d : null,
                Position: GetInt(reader, 10) ?? columns.Count + 1));
        }

        return columns;
    }

    private static int? GetInt(DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : Convert.ToInt32(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
}
