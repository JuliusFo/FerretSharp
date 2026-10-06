using System.Data.Common;
using System.Globalization;
using FerretSharp.Core.Connections;
using FerretSharp.Core.Data;
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

    // ALL_TAB_COLS without hidden columns is exactly ALL_TAB_COLUMNS, plus VIRTUAL_COLUMN.
    // All column names of a schema at once (C# model comparison): one round trip instead of one per table.
    private const string ColumnNamesSql = """
        SELECT table_name, column_name
          FROM all_tab_cols
         WHERE owner = :owner AND hidden_column = 'NO'
         ORDER BY table_name, column_id
        """;

    private const string ColumnsSql = """
        SELECT c.column_name, c.data_type, c.char_used, c.char_length, c.data_length, c.data_precision, c.data_scale,
               c.nullable, c.identity_column, c.data_default, c.column_id, cm.comments, c.virtual_column, c.default_on_null
          FROM all_tab_cols c
          LEFT JOIN all_col_comments cm
            ON cm.owner = c.owner AND cm.table_name = c.table_name AND cm.column_name = c.column_name
         WHERE c.owner = :owner AND c.table_name = :name AND c.hidden_column = 'NO'
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

    // SEARCH_CONDITION is LONG (fetched up to InitialLONGFetchSize); it may be selected but not sorted on.
    private const string ConstraintsSql = """
        SELECT c.constraint_name, c.constraint_type, c.search_condition, c.r_owner, r.table_name, c.delete_rule,
               c.status, c.validated, c.deferrable, c.deferred, c.generated
          FROM all_constraints c
          LEFT JOIN all_constraints r ON r.owner = c.r_owner AND r.constraint_name = c.r_constraint_name
         WHERE c.owner = :owner AND c.table_name = :name
         ORDER BY CASE c.constraint_type WHEN 'P' THEN 1 WHEN 'U' THEN 2 WHEN 'R' THEN 3 WHEN 'C' THEN 4 ELSE 5 END,
                  c.constraint_name
        """;

    private const string ConstraintColumnsSql = """
        SELECT constraint_name, column_name FROM all_cons_columns
         WHERE owner = :owner AND table_name = :name
         ORDER BY constraint_name, position
        """;

    private const string ReferencedColumnsSql = """
        SELECT c.constraint_name, rc.column_name
          FROM all_constraints c
          JOIN all_cons_columns rc ON rc.owner = c.r_owner AND rc.constraint_name = c.r_constraint_name
         WHERE c.owner = :owner AND c.table_name = :name AND c.constraint_type = 'R'
         ORDER BY c.constraint_name, rc.position
        """;

    // LOB indexes (SYS_IL…) belong to LOB columns and cannot be changed or used directly.
    private const string IndexesSql = """
        SELECT owner, index_name, index_type, uniqueness, status, tablespace_name, partitioned, visibility
          FROM all_indexes
         WHERE table_owner = :owner AND table_name = :name AND index_type <> 'LOB'
         ORDER BY index_name
        """;

    private const string IndexColumnsSql = """
        SELECT index_owner, index_name, column_name, column_position, descend
          FROM all_ind_columns
         WHERE table_owner = :owner AND table_name = :name
         ORDER BY index_owner, index_name, column_position
        """;

    // COLUMN_EXPRESSION is LONG: function-based columns and descending columns ("NAME" DESC is stored as an expression).
    private const string IndexExpressionsSql = """
        SELECT index_owner, index_name, column_position, column_expression
          FROM all_ind_expressions
         WHERE table_owner = :owner AND table_name = :name
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

    // TM locks on the table by other sessions; row locks themselves are not listed by Oracle.
    private const string LockHoldersSql = """
        SELECT DISTINCT s.sid, s.username, s.osuser, s.machine, s.program, s.module, s.action, s.logon_time
          FROM v$locked_object lo
          JOIN v$session s ON s.sid = lo.session_id
          JOIN all_objects o ON o.object_id = lo.object_id
         WHERE o.owner = :owner AND o.object_name = :name AND o.object_type = 'TABLE'
           AND s.sid <> SYS_CONTEXT('USERENV', 'SID')
         ORDER BY s.sid
        """;

    // A function call in a plain query: DBMS_METADATA only reads. Object type names use underscores here.
    private const string DdlSql = "SELECT DBMS_METADATA.GET_DDL(:object_type, :name, :owner) FROM DUAL";

    public async Task<IReadOnlyList<TableSummary>> GetTablesAsync(string owner, CancellationToken cancellationToken)
    {
        var invalid = await session.ExecuteReaderAsync(InvalidObjectsSql, [new("owner", owner)], async (reader, ct) =>
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            while (await reader.ReadAsync(ct))
            {
                names.Add(reader.GetString(0));
            }

            return names;
        }, cancellationToken);

        return await session.ExecuteReaderAsync(TablesSql, [new("owner", owner)], async (reader, ct) =>
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
                var name = reader.GetString(0);
                tables.Add(new TableSummary(owner, name, kind) { IsInvalid = kind != TableKind.Table && invalid.Contains(name) });
            }

            tables.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
            return (IReadOnlyList<TableSummary>)tables;
        }, cancellationToken);
    }

    public Task<IReadOnlyList<TableSummary>> GetSynonymTargetsAsync(string owner, CancellationToken cancellationToken) =>
        session.ExecuteReaderAsync(SynonymsSql, [new("owner", owner)], async (reader, ct) =>
        {
            var result = new List<TableSummary>();
            while (await reader.ReadAsync(ct))
            {
                var kind = GetInt(reader, 4) switch
                {
                    3 => TableKind.MaterializedView,
                    2 => TableKind.View,
                    _ => TableKind.Table,
                };
                result.Add(new TableSummary(reader.GetString(2), reader.GetString(3), kind, new SynonymInfo(reader.GetString(0), reader.GetString(1)))
                {
                    IsInvalid = GetInt(reader, 5) == 1,
                });
            }

            return (IReadOnlyList<TableSummary>)result;
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
                Position: GetInt(reader, 10) ?? columns.Count + 1,
                Comment: GetText(reader, 11),
                IsVirtual: GetText(reader, 12) == "YES",
                DefaultOnNull: GetText(reader, 13) == "YES"));
        }

        return columns;
    }

    public Task<ObjectInfo> GetObjectInfoAsync(TableSummary table, CancellationToken cancellationToken) =>
        session.ExecuteReaderAsync(ObjectInfoSql, [new("owner", table.Owner), new("name", table.Name), new("object_type", ObjectType(table.Kind))], async (reader, ct) =>
        {
            if (!await reader.ReadAsync(ct))
            {
                return new ObjectInfo("N/A", null, null, null, null, null, null, false, false);
            }

            return new ObjectInfo(
                Status: reader.GetString(0),
                Created: GetDate(reader, 1),
                LastDdl: GetDate(reader, 2),
                Comment: (table.Kind == TableKind.MaterializedView ? GetText(reader, 4) : null) ?? GetText(reader, 3),
                NumRows: reader.IsDBNull(5) ? null : Convert.ToInt64(reader.GetValue(5), CultureInfo.InvariantCulture),
                LastAnalyzed: GetDate(reader, 6),
                Tablespace: GetText(reader, 7),
                Partitioned: GetText(reader, 8) == "YES",
                Temporary: GetText(reader, 9) == "Y");
        }, cancellationToken);

    public async Task<IReadOnlyList<ConstraintInfo>> GetConstraintsAsync(TableRef table, CancellationToken cancellationToken)
    {
        QueryParameter[] parameters = [new("owner", table.Owner), new("name", table.Name)];
        var columns = await ReadNameListsAsync(ConstraintColumnsSql, parameters, cancellationToken);
        var referenced = await ReadNameListsAsync(ReferencedColumnsSql, parameters, cancellationToken);

        return await session.ExecuteReaderAsync(ConstraintsSql, parameters, async (reader, ct) =>
        {
            var result = new List<ConstraintInfo>();
            while (await reader.ReadAsync(ct))
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
                result.Add(new ConstraintInfo(
                    Name: name,
                    Type: type,
                    Columns: columns.GetValueOrDefault(name) ?? [],
                    Condition: type == ConstraintType.Check ? GetText(reader, 2)?.Trim() : null,
                    References: GetText(reader, 3) is { } refOwner && GetText(reader, 4) is { } refTable ? new TableRef(refOwner, refTable) : null,
                    ReferencedColumns: referenced.GetValueOrDefault(name) ?? [],
                    DeleteRule: type == ConstraintType.ForeignKey ? GetText(reader, 5) : null,
                    Enabled: GetText(reader, 6) == "ENABLED",
                    Validated: GetText(reader, 7) == "VALIDATED",
                    Deferrable: GetText(reader, 8) == "DEFERRABLE",
                    InitiallyDeferred: GetText(reader, 9) == "DEFERRED",
                    GeneratedName: GetText(reader, 10) == "GENERATED NAME"));
            }

            return (IReadOnlyList<ConstraintInfo>)result;
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<IndexInfo>> GetIndexesAsync(TableRef table, CancellationToken cancellationToken)
    {
        QueryParameter[] parameters = [new("owner", table.Owner), new("name", table.Name)];

        var columns = await session.ExecuteReaderAsync(IndexColumnsSql, parameters, async (reader, ct) =>
        {
            var result = new List<(string Owner, string Index, string Column, int Position, bool Descending)>();
            while (await reader.ReadAsync(ct))
            {
                result.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), GetInt(reader, 3) ?? 0, GetText(reader, 4) == "DESC"));
            }

            return result;
        }, cancellationToken);

        var expressions = await session.ExecuteReaderAsync(IndexExpressionsSql, parameters, async (reader, ct) =>
        {
            var result = new Dictionary<(string Owner, string Index, int Position), string>();
            while (await reader.ReadAsync(ct))
            {
                if (GetText(reader, 3) is { } expression)
                {
                    result[(reader.GetString(0), reader.GetString(1), GetInt(reader, 2) ?? 0)] = expression.Trim();
                }
            }

            return result;
        }, cancellationToken);

        var columnsByIndex = columns.ToLookup(c => (c.Owner, c.Index), c =>
            expressions.TryGetValue((c.Owner, c.Index, c.Position), out var expression)
                ? IndexColumnOf(expression, c.Descending)
                : new IndexColumn(c.Column, false, c.Descending));

        return await session.ExecuteReaderAsync(IndexesSql, parameters, async (reader, ct) =>
        {
            var result = new List<IndexInfo>();
            while (await reader.ReadAsync(ct))
            {
                var owner = reader.GetString(0);
                var name = reader.GetString(1);
                result.Add(new IndexInfo(
                    Owner: owner,
                    Name: name,
                    IndexType: reader.GetString(2),
                    Unique: GetText(reader, 3) == "UNIQUE",
                    Status: GetText(reader, 4) ?? "N/A",
                    Columns: columnsByIndex[(owner, name)].ToList(),
                    Tablespace: GetText(reader, 5),
                    Partitioned: GetText(reader, 6) == "YES",
                    Visible: GetText(reader, 7) != "INVISIBLE"));
            }

            return (IReadOnlyList<IndexInfo>)result;
        }, cancellationToken);
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

    public Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> GetColumnNamesAsync(string owner, CancellationToken cancellationToken) =>
        session.ExecuteReaderAsync(ColumnNamesSql, [new("owner", owner)], async (reader, ct) =>
        {
            // Thousands of rows: fetch them in a few round trips, not in the default 128 KB portions (slow over a VPN).
            if (reader is global::Oracle.ManagedDataAccess.Client.OracleDataReader oracle && oracle.RowSize > 0)
            {
                oracle.FetchSize = oracle.RowSize * 5000;
            }

            var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            while (await reader.ReadAsync(ct))
            {
                var table = reader.GetString(0);
                if (!result.TryGetValue(table, out var columns))
                {
                    result[table] = columns = [];
                }

                columns.Add(reader.GetString(1));
            }

            return (IReadOnlyDictionary<string, IReadOnlyList<string>>)result.ToDictionary(e => e.Key, e => (IReadOnlyList<string>)e.Value, StringComparer.Ordinal);
        }, cancellationToken);

    public async Task<ExecutionPlan> ExplainAsync(QuerySpec query, CancellationToken cancellationToken)
    {
        try
        {
            var steps = await session.ExplainPlanAsync(query.Sql, (reader, ct) => OraclePlans.ReadAsync(reader, actual: false, ct), cancellationToken);
            return new ExecutionPlan(PlanSource.Estimated, query.Sql, steps);
        }
        catch (InvalidOperationException ex)
        {
            throw new PlanUnavailableException(ex.Message, ex);
        }
    }

    public async Task<IReadOnlyList<LockHolder>?> GetLockHoldersAsync(TableRef table, CancellationToken cancellationToken)
    {
        try
        {
            return await session.ExecuteReaderAsync(LockHoldersSql, [new("owner", table.Owner), new("name", table.Name)], async (reader, ct) =>
            {
                var result = new List<LockHolder>();
                while (await reader.ReadAsync(ct))
                {
                    result.Add(new LockHolder(
                        GetInt(reader, 0) ?? 0, GetText(reader, 1), GetText(reader, 2), GetText(reader, 3), GetText(reader, 4),
                        GetText(reader, 5), GetText(reader, 6), GetDate(reader, 7)));
                }

                return (IReadOnlyList<LockHolder>?)result;
            }, cancellationToken);
        }
        catch (DatabaseException ex) when (ex.IsAny(OracleErrorCodes.MissingRights))
        {
            return null; // no access to the V$ views
        }
    }

    /// <summary>A descending column is stored as the quoted column name ("NAME"); anything else is a real expression.</summary>
    private static IndexColumn IndexColumnOf(string expression, bool descending) =>
        expression.Length > 2 && expression[0] == '"' && expression[^1] == '"' && expression.IndexOf('"', 1) == expression.Length - 1
            ? new IndexColumn(expression[1..^1], false, descending)
            : new IndexColumn(expression, true, descending);

    private Task<Dictionary<string, IReadOnlyList<string>>> ReadNameListsAsync(
        string sql, IReadOnlyList<QueryParameter> parameters, CancellationToken cancellationToken) =>
        session.ExecuteReaderAsync(sql, parameters, async (reader, ct) =>
        {
            var rows = new List<(string Key, string Value)>();
            while (await reader.ReadAsync(ct))
            {
                rows.Add((reader.GetString(0), reader.GetString(1)));
            }

            return rows.GroupBy(r => r.Key).ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(r => r.Value).ToList());
        }, cancellationToken);

    private Task<IReadOnlyList<DependencyInfo>> ReadDependenciesAsync(
        string sql, IReadOnlyList<QueryParameter> parameters, CancellationToken cancellationToken) =>
        session.ExecuteReaderAsync(sql, parameters, async (reader, ct) =>
        {
            var result = new List<DependencyInfo>();
            while (await reader.ReadAsync(ct))
            {
                result.Add(new DependencyInfo(reader.GetString(0), reader.GetString(1), reader.GetString(2), GetText(reader, 3)));
            }

            return (IReadOnlyList<DependencyInfo>)result;
        }, cancellationToken);

    private static string ObjectType(TableKind kind) => kind switch
    {
        TableKind.View => "VIEW",
        TableKind.MaterializedView => "MATERIALIZED VIEW",
        _ => "TABLE",
    };

    private static string? GetText(DbDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static DateTime? GetDate(DbDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetDateTime(ordinal);

    private static int? GetInt(DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : Convert.ToInt32(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
}
