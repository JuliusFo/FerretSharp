using System.Data.Common;
using FerretSharp.Core.Query;
using FerretSharp.Core.Schema;
using static FerretSharp.Core.Oracle.OracleReading;

namespace FerretSharp.Core.Oracle;

/// <summary>
/// Reads schema metadata from the ALL_* views. Every query is restricted to one owner. Split by topic: the catalog
/// (this file: object list, synonyms, foreign keys, one table's columns and keys), object details
/// (<c>.ObjectDetails.cs</c>), the whole-schema snapshot (<c>.Snapshot.cs</c>) and diagnostics on the V$ views and
/// plans (<c>.Diagnostics.cs</c>). The select lists and their mapping to records live here, shared by the per-table
/// statements and the snapshot, so both always read the same.
/// </summary>
public sealed partial class OracleSchemaReader(OracleSession session) : ISchemaReader
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

    // Views and mviews that need recompiling (e.g. a column of their table was dropped); tables are never invalid.
    private const string InvalidObjectsSql = """
        SELECT object_name FROM all_objects
         WHERE owner = :owner AND status = 'INVALID' AND object_type IN ('VIEW', 'MATERIALIZED VIEW')
        """;

    // Private synonyms of the schema and public synonyms; targets must be tables/views/mviews the session can see
    // (ALL_OBJECTS), outside the schema itself and outside Oracle-maintained schemas (filters thousands of SYS
    // synonyms). An mview shows up in ALL_OBJECTS as TABLE and MATERIALIZED VIEW, hence MAX over a rank.
    private const string SynonymsSql = """
        SELECT s.owner, s.synonym_name, s.table_owner, s.table_name,
               MAX(CASE o.object_type WHEN 'MATERIALIZED VIEW' THEN 3 WHEN 'VIEW' THEN 2 ELSE 1 END) AS kind,
               MAX(CASE WHEN o.status = 'INVALID' AND o.object_type <> 'TABLE' THEN 1 ELSE 0 END) AS invalid
          FROM all_synonyms s
          JOIN all_objects o
            ON o.owner = s.table_owner AND o.object_name = s.table_name
           AND o.object_type IN ('TABLE', 'VIEW', 'MATERIALIZED VIEW')
          JOIN all_users u
            ON u.username = s.table_owner AND u.oracle_maintained = 'N'
         WHERE s.db_link IS NULL
           AND (s.owner = :owner OR s.owner = 'PUBLIC')
           AND s.table_owner <> :owner
         GROUP BY s.owner, s.synonym_name, s.table_owner, s.table_name
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

    // ---- shared select lists: one owner, narrowed to one table with ByTable (…TableOrdinal is the table name) ----

    /// <summary>The column list of <see cref="ColumnsSelect"/>; the light variant of the snapshot replaces two of them.</summary>
    private const string ColumnFields = """
        c.column_name, c.data_type, c.char_used, c.char_length, c.data_length, c.data_precision, c.data_scale,
               c.nullable, c.identity_column, c.data_default, c.column_id, cm.comments, c.virtual_column, c.default_on_null,
               c.table_name
        """;

    // ALL_TAB_COLS without hidden columns is exactly ALL_TAB_COLUMNS, plus VIRTUAL_COLUMN.
    private const string ColumnsSelect = $"""
        SELECT {ColumnFields}
          FROM all_tab_cols c
          LEFT JOIN all_col_comments cm
            ON cm.owner = c.owner AND cm.table_name = c.table_name AND cm.column_name = c.column_name
         WHERE c.owner = :owner AND c.hidden_column = 'NO'
        """;

    private const int ColumnsTableOrdinal = 14;

    // SEARCH_CONDITION is LONG (fetched up to InitialLONGFetchSize); it may be selected but not sorted on.
    private const string ConstraintsSelect = """
        SELECT c.constraint_name, c.constraint_type, c.search_condition, c.r_owner, r.table_name, c.delete_rule,
               c.status, c.validated, c.deferrable, c.deferred, c.generated, c.table_name
          FROM all_constraints c
          LEFT JOIN all_constraints r ON r.owner = c.r_owner AND r.constraint_name = c.r_constraint_name
         WHERE c.owner = :owner
        """;

    private const int ConstraintsTableOrdinal = 11;

    private const string ConstraintOrder = """
        CASE c.constraint_type WHEN 'P' THEN 1 WHEN 'U' THEN 2 WHEN 'R' THEN 3 WHEN 'C' THEN 4 ELSE 5 END, c.constraint_name
        """;

    private const string ConstraintColumnsSelect = "SELECT constraint_name, column_name FROM all_cons_columns WHERE owner = :owner";

    private const string ReferencedColumnsSelect = """
        SELECT c.constraint_name, rc.column_name
          FROM all_constraints c
          JOIN all_cons_columns rc ON rc.owner = c.r_owner AND rc.constraint_name = c.r_constraint_name
         WHERE c.owner = :owner AND c.constraint_type = 'R'
        """;

    // LOB indexes (SYS_IL…) belong to LOB columns and cannot be changed or used directly.
    private const string IndexesSelect = """
        SELECT owner, index_name, index_type, uniqueness, status, tablespace_name, partitioned, visibility, table_name
          FROM all_indexes
         WHERE table_owner = :owner AND index_type <> 'LOB'
        """;

    private const int IndexesTableOrdinal = 8;

    private const string IndexColumnsSelect = """
        SELECT index_owner, index_name, column_name, column_position, descend
          FROM all_ind_columns
         WHERE table_owner = :owner
        """;

    // COLUMN_EXPRESSION is LONG: function-based columns and descending columns ("NAME" DESC is stored as an expression).
    private const string IndexExpressionsSelect = """
        SELECT index_owner, index_name, column_position, column_expression
          FROM all_ind_expressions
         WHERE table_owner = :owner
        """;

    // ---- one table ----

    private const string ColumnsSql = $"""
        {ColumnsSelect}
           AND c.table_name = :name
         ORDER BY c.column_id
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

    // TEXT and QUERY are LONG columns: fetched up to the session's InitialLONGFetchSize (32767 characters).
    private const string ViewDefinitionSql = "SELECT text FROM all_views WHERE owner = :owner AND view_name = :name";

    private const string MViewDefinitionSql = "SELECT query FROM all_mviews WHERE owner = :owner AND mview_name = :name";

    public async Task<IReadOnlyList<TableSummary>> GetTablesAsync(string owner, CancellationToken cancellationToken)
    {
        var invalid = (await session.ReadListAsync(InvalidObjectsSql, [new("owner", owner)], r => r.GetString(0), cancellationToken))
            .ToHashSet(StringComparer.Ordinal);

        var tables = await session.ReadListAsync(TablesSql, [new("owner", owner)], reader =>
        {
            var kind = reader.GetString(1) switch
            {
                "VIEW" => TableKind.View,
                "MVIEW" => TableKind.MaterializedView,
                _ => TableKind.Table,
            };
            var name = reader.GetString(0);
            return new TableSummary(owner, name, kind) { IsInvalid = kind != TableKind.Table && invalid.Contains(name) };
        }, cancellationToken);

        tables.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        return tables;
    }

    public async Task<IReadOnlyList<TableSummary>> GetSynonymTargetsAsync(string owner, CancellationToken cancellationToken) =>
        await session.ReadListAsync(SynonymsSql, [new("owner", owner)], reader =>
        {
            var kind = Int(reader, 4) switch
            {
                3 => TableKind.MaterializedView,
                2 => TableKind.View,
                _ => TableKind.Table,
            };
            return new TableSummary(reader.GetString(2), reader.GetString(3), kind, new SynonymInfo(reader.GetString(0), reader.GetString(1)))
            {
                IsInvalid = Int(reader, 5) == 1,
            };
        }, cancellationToken);

    public async Task<IReadOnlyList<ForeignKeyInfo>> GetForeignKeysAsync(string owner, CancellationToken cancellationToken)
    {
        var rows = await session.ReadListAsync(ForeignKeysSql, [new("owner", owner)], reader =>
            (Name: reader.GetString(0), Table: reader.GetString(1), Column: reader.GetString(2),
             RefOwner: reader.GetString(3), RefTable: reader.GetString(4), RefColumn: reader.GetString(5)), cancellationToken);

        return rows
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
    }

    public async Task<TableDetails> GetDetailsAsync(TableSummary table, CancellationToken cancellationToken)
    {
        QueryParameter[] parameters = [new("owner", table.Owner), new("name", table.Name)];

        var columns = await ReadColumnsAsync(ColumnsSql, parameters, many: false, cancellationToken);
        var keys = await session.ReadListAsync(KeysSql, parameters, reader =>
            (Type: reader.GetString(0), Name: reader.GetString(1), Column: reader.GetString(2)), cancellationToken);

        var isIot = table.Kind == TableKind.Table && await session.ExecuteReaderAsync(IotSql, parameters, async (reader, ct) =>
            await reader.ReadAsync(ct) && !reader.IsDBNull(0) && reader.GetString(0) == "IOT", cancellationToken);

        var primaryKey = keys.Where(k => k.Type == "P").Select(k => k.Column).ToList();
        var uniqueKeys = keys
            .Where(k => k.Type == "U")
            .GroupBy(k => k.Name)
            .Select(g => (IReadOnlyList<string>)g.Select(k => k.Column).ToList())
            .ToList();

        var definitionSql = table.Kind switch
        {
            TableKind.View => ViewDefinitionSql,
            TableKind.MaterializedView => MViewDefinitionSql,
            _ => null,
        };
        var (definition, truncated) = definitionSql is null
            ? (null, false)
            : await session.ExecuteReaderAsync(definitionSql, parameters, async (reader, ct) =>
            {
                if (!await reader.ReadAsync(ct) || reader.IsDBNull(0))
                {
                    return ((string?)null, false);
                }

                // TEXT_LENGTH counts bytes, so compare with the fetch limit instead (multi-byte text would look truncated).
                var raw = reader.GetString(0);
                return (raw.Trim(), raw.Length >= OracleSession.LongFetchSize);
            }, cancellationToken);

        return new TableDetails(table, columns, primaryKey, uniqueKeys, isIot, definition, truncated);
    }

    // ---- reading the shared select lists ----

    /// <summary>The columns of one table, in order.</summary>
    private async Task<IReadOnlyList<ColumnInfo>> ReadColumnsAsync(
        string sql, IReadOnlyList<QueryParameter> parameters, bool many, CancellationToken cancellationToken) =>
        (await ReadColumnsByTableAsync(sql, parameters, many, cancellationToken)).Values.SingleOrDefault() ?? [];

    /// <summary>Columns by table name (statements over <see cref="ColumnFields"/>, ordered by table and column id).</summary>
    private Task<Dictionary<string, List<ColumnInfo>>> ReadColumnsByTableAsync(
        string sql, IReadOnlyList<QueryParameter> parameters, bool many, CancellationToken cancellationToken) =>
        session.ExecuteReaderAsync(sql, parameters, async (reader, ct) =>
        {
            if (many)
            {
                FetchManyRows(reader);
            }

            var result = new Dictionary<string, List<ColumnInfo>>(StringComparer.Ordinal);
            while (await reader.ReadAsync(ct))
            {
                var list = ListOf(result, reader.GetString(ColumnsTableOrdinal));
                list.Add(ReadColumn(reader, list.Count + 1));
            }

            return result;
        }, cancellationToken);

    /// <summary>One row over <see cref="ColumnFields"/>.</summary>
    private static ColumnInfo ReadColumn(DbDataReader reader, int fallbackPosition)
    {
        var dataType = reader.GetString(1);
        var charSemantics = !reader.IsDBNull(2) && reader.GetString(2) == "C";
        var charLength = Int(reader, 3);
        var dataLength = Int(reader, 4);
        int? length = dataType switch
        {
            "VARCHAR2" or "CHAR" => charSemantics ? charLength : dataLength,
            "NVARCHAR2" or "NCHAR" => charLength,
            "RAW" => dataLength,
            _ => null,
        };

        return new ColumnInfo(
            Name: reader.GetString(0),
            DataType: dataType,
            Length: length,
            CharSemantics: charSemantics,
            Precision: Int(reader, 5),
            Scale: Int(reader, 6),
            Nullable: reader.GetString(7) == "Y",
            IsIdentity: !reader.IsDBNull(8) && reader.GetString(8) == "YES",
            Default: reader.IsDBNull(9) ? null : reader.GetString(9).Trim() is { Length: > 0 } d ? d : null,
            Position: Int(reader, 10) ?? fallbackPosition,
            Comment: Text(reader, 11),
            IsVirtual: Text(reader, 12) == "YES",
            DefaultOnNull: Text(reader, 13) == "YES");
    }

    /// <summary>One row of <see cref="ConstraintsSelect"/>; the column lists are keyed by constraint name.</summary>
    private static ConstraintInfo ReadConstraint(
        DbDataReader reader, Dictionary<string, IReadOnlyList<string>> columns, Dictionary<string, IReadOnlyList<string>> referenced)
    {
        var name = reader.GetString(0);
        var type = reader.GetString(1) switch
        {
            "P" => ConstraintType.PrimaryKey,
            "U" => ConstraintType.Unique,
            "R" => ConstraintType.ForeignKey,
            "C" => ConstraintType.Check,
            "V" => ConstraintType.ViewCheckOption,
            "O" => ConstraintType.ViewReadOnly,
            _ => ConstraintType.Other,
        };
        return new ConstraintInfo(
            Name: name,
            Type: type,
            Columns: columns.GetValueOrDefault(name) ?? [],
            Condition: type == ConstraintType.Check ? Text(reader, 2)?.Trim() : null,
            References: Text(reader, 3) is { } refOwner && Text(reader, 4) is { } refTable ? new TableRef(refOwner, refTable) : null,
            ReferencedColumns: referenced.GetValueOrDefault(name) ?? [],
            DeleteRule: type == ConstraintType.ForeignKey ? Text(reader, 5) : null,
            Enabled: Text(reader, 6) == "ENABLED",
            Validated: Text(reader, 7) == "VALIDATED",
            Deferrable: Text(reader, 8) == "DEFERRABLE",
            InitiallyDeferred: Text(reader, 9) == "DEFERRED",
            GeneratedName: Text(reader, 10) == "GENERATED NAME");
    }

    /// <summary>Index columns by (index owner, index name), expressions and descending columns resolved.</summary>
    private async Task<ILookup<(string Owner, string Index), IndexColumn>> ReadIndexColumnsAsync(
        string columnsSql, string expressionsSql, IReadOnlyList<QueryParameter> parameters, bool many, CancellationToken cancellationToken)
    {
        var columns = await session.ReadListAsync(columnsSql, parameters, reader =>
            (Owner: reader.GetString(0), Index: reader.GetString(1), Column: reader.GetString(2), Position: Int(reader, 3) ?? 0,
             Descending: Text(reader, 4) == "DESC"), cancellationToken, many);

        var expressions = (await session.ReadListAsync(expressionsSql, parameters, reader =>
                (Key: (Owner: reader.GetString(0), Index: reader.GetString(1), Position: Int(reader, 2) ?? 0), Expression: Text(reader, 3)),
                cancellationToken))
            .Where(e => e.Expression is not null)
            .ToDictionary(e => e.Key, e => e.Expression!.Trim());

        return columns.ToLookup(c => (c.Owner, c.Index), c =>
            expressions.TryGetValue((c.Owner, c.Index, c.Position), out var expression)
                ? IndexColumnOf(expression, c.Descending)
                : new IndexColumn(c.Column, false, c.Descending));
    }

    /// <summary>One row of <see cref="IndexesSelect"/>.</summary>
    private static IndexInfo ReadIndex(DbDataReader reader, ILookup<(string Owner, string Index), IndexColumn> columnsByIndex)
    {
        var owner = reader.GetString(0);
        var name = reader.GetString(1);
        return new IndexInfo(
            Owner: owner,
            Name: name,
            IndexType: reader.GetString(2),
            Unique: Text(reader, 3) == "UNIQUE",
            Status: Text(reader, 4) ?? "N/A",
            Columns: columnsByIndex[(owner, name)].ToList(),
            Tablespace: Text(reader, 5),
            Partitioned: Text(reader, 6) == "YES",
            Visible: Text(reader, 7) != "INVISIBLE");
    }

    /// <summary>A descending column is stored as the quoted column name ("NAME"); anything else is a real expression.</summary>
    private static IndexColumn IndexColumnOf(string expression, bool descending) =>
        expression.Length > 2 && expression[0] == '"' && expression[^1] == '"' && expression.IndexOf('"', 1) == expression.Length - 1
            ? new IndexColumn(expression[1..^1], false, descending)
            : new IndexColumn(expression, true, descending);

    /// <summary>Two-column rows (key, value) as value lists by key, in row order.</summary>
    private async Task<Dictionary<string, IReadOnlyList<string>>> ReadNameListsAsync(
        string sql, IReadOnlyList<QueryParameter> parameters, CancellationToken cancellationToken, bool many = false) =>
        (await session.ReadListAsync(sql, parameters, reader => (Key: reader.GetString(0), Value: reader.GetString(1)), cancellationToken, many))
            .GroupBy(r => r.Key)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(r => r.Value).ToList());

    private static List<T> ListOf<T>(Dictionary<string, List<T>> lists, string key)
    {
        if (!lists.TryGetValue(key, out var list))
        {
            lists[key] = list = [];
        }

        return list;
    }

    private static string ObjectType(TableKind kind) => kind switch
    {
        TableKind.View => "VIEW",
        TableKind.MaterializedView => "MATERIALIZED VIEW",
        _ => "TABLE",
    };
}
