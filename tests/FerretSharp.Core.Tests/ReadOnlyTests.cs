using System.Reflection;
using FerretSharp.Core.Connections;
using FerretSharp.Core.Data;
using FerretSharp.Core.Oracle;
using FerretSharp.Core.Query;
using FerretSharp.Core.Schema;

namespace FerretSharp.Core.Tests;

/// <summary>
/// Reading stays separate from writing (CLAUDE.md, section 2; ADR 0006): the query path refuses anything but plain
/// queries, every statement FerretSharp builds passes that guard, the only write path is internal and refuses DDL, the
/// schema path (WP-22) is internal and takes nothing but single DDL statements, and transaction control exists on the session only.
/// </summary>
public class ReadOnlyTests
{
    [Theory]
    [InlineData("SELECT 1 FROM DUAL")]
    [InlineData("select * from t")]
    [InlineData("  \r\n SELECT * FROM t")]
    [InlineData("-- comment\nSELECT * FROM t")]
    [InlineData("/* multi\nline */ WITH x AS (SELECT 1 FROM DUAL) SELECT * FROM x")]
    [InlineData("SELECT ';' AS semicolon, 'FOR UPDATE' AS text FROM DUAL")]
    [InlineData("SELECT * FROM t;")]
    [InlineData("SELECT \"FOR\" FROM t WHERE \"UPDATE_DATE\" > :p0")]
    // SQL editor (WP-17): comments and literals the editor accepts must not look like a second statement or a lock
    [InlineData("SELECT * FROM t -- the end;\nWHERE id = 1")]
    [InlineData("SELECT * FROM t /* not for update */")]
    [InlineData("SELECT q'[a;b]', q'{for update}' FROM DUAL")]
    [InlineData("SELECT \"A;B\" FROM t")]
    [InlineData("SELECT * FROM t;;")]
    public void Plain_queries_are_allowed(string sql) => Assert.True(OracleSession.IsReadOnlyStatement(sql), sql);

    [Theory]
    [InlineData("DELETE FROM t")]
    [InlineData("INSERT INTO t VALUES (1)")]
    [InlineData("UPDATE t SET c = 1")]
    [InlineData("MERGE INTO t USING s ON (1 = 1) WHEN MATCHED THEN UPDATE SET c = 1")]
    [InlineData("BEGIN DELETE FROM t; END;")]
    [InlineData("DECLARE x NUMBER; BEGIN NULL; END;")]
    [InlineData("CALL p()")]
    [InlineData("ALTER SESSION SET CURRENT_SCHEMA = X")]
    [InlineData("DROP TABLE t")]
    [InlineData("TRUNCATE TABLE t")]
    [InlineData("LOCK TABLE t IN EXCLUSIVE MODE")]
    [InlineData("COMMIT")]
    [InlineData("SELECT * FROM t FOR UPDATE")]
    [InlineData("SELECT * FROM t for  update nowait")]
    [InlineData("SELECT 1 FROM DUAL; DELETE FROM t")]
    [InlineData("-- SELECT\nDELETE FROM t")]
    [InlineData("/* SELECT */ DELETE FROM t")]
    [InlineData("SELECTED_ROWS")]
    [InlineData("")]
    [InlineData("-- only a comment")]
    [InlineData("SELECT * FROM t /* ; */ ; DELETE FROM t")]
    [InlineData("SELECT q'[x]' FROM t; DELETE FROM t")]
    [InlineData("SELECT * FROM t FOR /* lock */ UPDATE")]
    [InlineData("(SELECT * FROM t)")]
    public void Everything_else_is_refused(string sql) => Assert.False(OracleSession.IsReadOnlyStatement(sql), sql);

    [Theory]
    [InlineData("INSERT INTO t (a) VALUES (:p0)")]
    [InlineData("  update t set c = :p0 where id = :p1")]
    [InlineData("-- flush\nDELETE FROM t WHERE ROWID = :rid")]
    [InlineData("UPDATE t SET c = 'a;b' WHERE id = 1")]
    [InlineData("MERGE INTO t USING s ON (t.id = s.id) WHEN MATCHED THEN UPDATE SET c = s.c")] // SQL editor (ADR 0014)
    public void Write_path_accepts_single_dml(string sql) => Assert.True(OracleSession.IsWriteStatement(sql), sql);

    [Theory]
    [InlineData("CREATE TABLE t (id NUMBER)")]
    [InlineData("DROP TABLE t")]
    [InlineData("TRUNCATE TABLE t")]
    [InlineData("ALTER TABLE t ADD c NUMBER")]
    [InlineData("GRANT SELECT ON t TO x")]
    [InlineData("COMMENT ON TABLE t IS 'x'")]
    [InlineData("MERGE INTO t USING s ON (1 = 1) WHEN MATCHED THEN UPDATE SET c = 1; DROP TABLE t")]
    [InlineData("BEGIN DELETE FROM t; END;")]
    [InlineData("SELECT * FROM t")]
    [InlineData("COMMIT")]
    [InlineData("DELETE FROM t; DROP TABLE t")]
    [InlineData("/* DELETE */ DROP TABLE t")]
    [InlineData("")]
    [InlineData("DELETE FROM t WHERE c = q'[;]'; DROP TABLE t")]
    [InlineData("DELETE FROM t -- ;\n; DROP TABLE t")]
    public void Write_path_refuses_ddl_plsql_and_everything_else(string sql) => Assert.False(OracleSession.IsWriteStatement(sql), sql);

    /// <summary>WP-22, ADR 0019: the schema path takes DDL the SQL editor runs – a single statement, nothing else.</summary>
    [Theory]
    [InlineData("CREATE TABLE t (id NUMBER PRIMARY KEY, name VARCHAR2(40 CHAR))")]
    [InlineData("  alter table t add (c number default 0 not null)")]
    [InlineData("-- schema\nDROP TABLE t")]
    [InlineData("COMMENT ON COLUMN t.c IS 'a;b'")]
    [InlineData("CREATE OR REPLACE VIEW v AS SELECT * FROM t")]
    [InlineData("CREATE INDEX t_c ON t (c);")]
    [InlineData("GRANT SELECT ON t TO x")]
    [InlineData("ALTER TRIGGER t_bi DISABLE")]
    [InlineData("CREATE TRIGGER t_bi BEFORE INSERT ON t BEGIN NULL; END;")] // WP-26, ADR 0021
    [InlineData("""
        CREATE OR REPLACE TRIGGER "APP"."KUNDE_AUDIT"
        AFTER DELETE OR INSERT OR UPDATE ON KUNDE
        REFERENCING OLD AS OLD NEW AS NEW
        FOR EACH ROW
        DECLARE
        trans CHAR(1);
        BEGIN
        IF DELETING THEN
        INSERT INTO AUDIT_KUNDE (AUDIT_ID, AUDIT_TRANS, KUNDE_ID) VALUES (AUDIT_ID_SEQ.NEXTVAL, 'D', :old.KUNDE_ID);
        ELSE
        IF INSERTING THEN trans := 'I';
        ELSIF :new.NAME <> :old.NAME OR (:new.NAME IS NULL AND :old.NAME IS NOT NULL) THEN trans := 'U';
        ELSE GOTO ende;
        END IF;
        INSERT INTO AUDIT_KUNDE (AUDIT_ID, AUDIT_TRANS, KUNDE_ID) VALUES (AUDIT_ID_SEQ.NEXTVAL, trans, :new.KUNDE_ID);
        END IF;
        <<ende>>
        NULL;
        END;
        """)]
    public void Schema_path_accepts_single_ddl(string sql) => Assert.True(OracleSession.IsDdlStatement(sql), sql);

    [Theory]
    [InlineData("TRUNCATE TABLE t")]
    [InlineData("ALTER TABLE t TRUNCATE PARTITION p2025")] // review 3.31.0, A9: like TRUNCATE
    [InlineData("ALTER TABLE t DROP SUBPARTITION sp1")]
    [InlineData("FLASHBACK TABLE t TO TIMESTAMP SYSTIMESTAMP")]
    [InlineData("PURGE RECYCLEBIN")]
    [InlineData("-- schema\nDROP TABLE t PURGE")] // review 3.40.0, SE-8: past the recycle bin
    [InlineData("DROP USER app CASCADE")]
    [InlineData("DROP TABLESPACE ts INCLUDING CONTENTS")] // review 3.44.0, A14
    [InlineData("DROP CLUSTER c INCLUDING TABLES")]
    [InlineData("DROP PLUGGABLE DATABASE pdb1 INCLUDING DATAFILES")]
    [InlineData("ALTER DATABASE CLOSE")]
    [InlineData("ALTER SESSION SET CURRENT_SCHEMA = X")]
    [InlineData("ALTER SYSTEM KILL SESSION '1,2'")]
    [InlineData("CREATE OR REPLACE PROCEDURE p AS BEGIN NULL; END;")]
    [InlineData("CREATE OR REPLACE PACKAGE BODY p AS END;")]
    [InlineData("CREATE OR REPLACE TRIGGER x AFTER LOGON ON DATABASE BEGIN NULL; END;")]
    [InlineData("CREATE TRIGGER x BEFORE DDL ON app.SCHEMA BEGIN NULL; END;")]
    [InlineData("-- CREATE TRIGGER t BEFORE INSERT ON t\nBEGIN NULL; END;")]
    [InlineData("BEGIN EXECUTE IMMEDIATE 'DROP TABLE t'; END;")]
    [InlineData("CALL p()")]
    [InlineData("COMMIT")]
    [InlineData("DELETE FROM t")]
    [InlineData("INSERT INTO t VALUES (1)")]
    [InlineData("SELECT * FROM t")]
    [InlineData("CREATE TABLE t (id NUMBER); DROP TABLE u")]
    [InlineData("DROP TABLE t; DELETE FROM u")]
    [InlineData("COMMENT ON TABLE t IS 'x' -- ;\n; TRUNCATE TABLE t")]
    [InlineData("/* CREATE */ TRUNCATE TABLE t")]
    [InlineData("ALTER TABLE t MODIFY c DEFAULT :p0")]
    [InlineData("")]
    public void Schema_path_refuses_truncate_plsql_binds_and_everything_else(string sql) => Assert.False(OracleSession.IsDdlStatement(sql), sql);

    private static List<string> PlainPublicMethods(Type type) =>
        type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName)
            .Select(m => m.Name)
            .Distinct()
            .Order(StringComparer.Ordinal)
            .ToList();

    public static TheoryData<Type, string[]> PublicSurfaces => new()
    {
        {
            typeof(OracleSession), [
                "OpenAsync", "DisposeAsync", "SetActionAsync", "PingAsync", "PingIfIdleAsync", "ExecuteReaderAsync",
                "UseReadOnlySnapshotsAsync", "StopReadOnlySnapshotsAsync", "RefreshSnapshotAsync",
                "BeginTransactionAsync", "SavepointAsync", "RollbackToSavepointAsync", "CommitAsync", "RollbackAsync",
            ]
        },
        { typeof(IDataAccess), ["CountAsync", "ExplainActualAsync", "ReadAllAsync", "ReadColumnAllAsync", "ReadLobAsync", "ReadPageAsync", "ReadSqlAllAsync", "ReadSqlAsync"] },
        { typeof(IRowLookup), ["LookupAsync"] },
        {
            typeof(ISchemaReader), [
                "ExplainAsync", "GetColumnsAsync", "GetConstraintsAsync", "GetCursorAsync", "GetDdlAsync", "GetDependenciesAsync",
                "GetDetailsAsync", "GetErrorsAsync", "GetForeignKeysAsync", "GetIndexesAsync", "GetLockHoldersAsync", "GetObjectInfoAsync",
                "GetPlSqlInfoAsync", "GetPlSqlObjectsAsync", "GetPrimaryKeysAsync", "GetSessionActivityAsync", "GetSourceAsync", "GetSubprogramsAsync", "GetSynonymTargetsAsync",
                "GetTablesAsync", "GetTriggerColumnsAsync", "ReadSnapshotAsync", "SearchSourceAsync",
            ]
        },
        { typeof(IDatabaseConnection), ["PingIfIdleAsync", "SetActionAsync", "StopReadOnlySnapshotsAsync", "UseReadOnlySnapshotsAsync"] },
        { typeof(IDatabaseConnector), ["OpenAsync"] },
    };

    /// <summary>
    /// R5b: the exact public methods of the session and of the database interfaces – none writes, transaction control only
    /// on the session (not on what the UI sees). A new one fails here until it is added on purpose (review 3.40.0, C6: this
    /// replaced the checks by name, which only knew the names they listed).
    /// </summary>
    [Theory]
    [MemberData(nameof(PublicSurfaces))]
    public void Database_types_offer_exactly_the_known_methods(Type type, string[] expected) =>
        Assert.Equal(expected.Order(StringComparer.Ordinal), PlainPublicMethods(type));

    /// <summary>The Oracle classes behind the interfaces offer nothing beyond them (the UI gets the interface, a cast would not help).</summary>
    [Theory]
    [InlineData(typeof(OracleDataAccess), typeof(IDataAccess))]
    [InlineData(typeof(OracleSchemaReader), typeof(ISchemaReader))]
    public void Oracle_classes_offer_nothing_beyond_their_interface(Type type, Type contract) =>
        Assert.Empty(PlainPublicMethods(type).Except(contract.GetInterfaces().Prepend(contract).SelectMany(PlainPublicMethods)));

    /// <summary>R5b: every path that runs a statement past the guarded reader stays internal – writes, DDL, EXPLAIN PLAN, locking reads.</summary>
    [Theory]
    [InlineData("ExecuteNonQueryAsync")]
    [InlineData("ExecuteDdlAsync")]
    [InlineData("ExplainPlanAsync")]
    [InlineData("ExecuteLockingReaderAsync")]
    public void Statements_past_the_guarded_reader_run_through_internal_methods_only(string name)
    {
        var methods = typeof(OracleSession).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Where(m => m.Name == name)
            .ToList();

        Assert.NotEmpty(methods);
        Assert.All(methods, m => Assert.True(m.IsAssembly, name));
    }

    /// <summary>
    /// Review 3.44.0, C3: the reads of an export (WP-54) – the streaming reader and the queries it runs while its cursor is
    /// open – check the statement themselves; only a test against a database covered the first, nothing the second.
    /// </summary>
    [Theory]
    [InlineData("DELETE FROM kunden")]
    [InlineData("SELECT * FROM kunden FOR UPDATE")]
    [InlineData("SELECT 1 FROM dual; DROP TABLE kunden")]
    public async Task The_nested_reads_of_an_export_refuse_anything_but_a_query(string sql)
    {
        Assert.Throws<InvalidOperationException>(() => OracleSession.GuardReadOnly(sql));
        var nested = new OracleSession.NestedReader(null!); // the guard comes before the session is touched
        await Assert.ThrowsAsync<InvalidOperationException>(() => nested.ReadAsync(sql, [], (_, _) => Task.FromResult(0), TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Review 3.44.0, C4: a read is repeated in a new snapshot only in a locked session, only for an unusable snapshot, a
    /// few times – and never after rows were handed on: the export would write them a second time.
    /// </summary>
    [Theory]
    [InlineData(true, false, 1, 1555, true)] // snapshot too old
    [InlineData(true, false, 1, 1466, true)] // definition changed
    [InlineData(true, false, 3, 1555, true)]
    [InlineData(true, false, 4, 1555, false)] // given up
    [InlineData(true, true, 1, 1555, false)] // rows already in the file
    [InlineData(false, false, 1, 1555, false)] // not locked: no snapshot of ours
    [InlineData(true, false, 1, 942, false)] // another error
    [InlineData(true, false, 1, null, false)]
    public void A_read_runs_again_in_a_new_snapshot_only_before_rows_were_handed_on(
        bool readOnlySnapshots, bool handedOn, int attempt, int? error, bool again) =>
        Assert.Equal(again, OracleSession.ShouldRetry(readOnlySnapshots, handedOn, attempt, error));

    [Fact]
    public void Session_runs_statements_only_through_the_guarded_reader()
    {
        var executing = typeof(OracleSession).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.Name.StartsWith("Execute", StringComparison.Ordinal))
            .Select(m => m.Name);

        Assert.Equal(["ExecuteReaderAsync"], executing);
    }

    /// <summary>
    /// Every query text of the Oracle layer – constants and static fields of any type there that start with SELECT or WITH
    /// (data dictionary, plans, V$ views, the schema snapshot) – must pass the guard. Not only fields named …Sql of one
    /// class: before R3b the plan statements and the snapshot's built statement were not checked.
    /// </summary>
    [Fact]
    public void Oracle_layer_query_texts_are_plain_queries()
    {
        var statements = typeof(OracleSession).Assembly.GetTypes()
            .Where(t => t.Namespace == typeof(OracleSession).Namespace)
            .SelectMany(t => t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                .Where(f => f.FieldType == typeof(string))
                .Select(f => (Name: $"{t.Name}.{f.Name}", Sql: (string?)f.GetValue(null))))
            .Where(s => s.Sql?.TrimStart() is { } sql
                        && (sql.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase) || sql.StartsWith("WITH", StringComparison.OrdinalIgnoreCase)))
            .ToList();

        Assert.Contains(statements, s => s.Name == "OraclePlans.ActualSteps");
        Assert.Contains(statements, s => s.Name == "OraclePlans.CursorFigures");
        Assert.Contains(statements, s => s.Name == "OracleSession.PlanTableSql");
        Assert.Contains(statements, s => s.Name == "OracleSchemaReader.SchemaColumnsSql");
        Assert.All(statements, s => Assert.True(OracleSession.IsReadOnlyStatement(s.Sql!), s.Name));
    }

    /// <summary>
    /// The light column list of the C# model comparison reads with the same mapping as the full one: built from it, so it
    /// keeps every position (before R3b it was a hand copy).
    /// </summary>
    [Fact]
    public void Schema_columns_statement_keeps_the_positions_of_the_column_list()
    {
        static string Field(string name) =>
            (string)typeof(OracleSchemaReader).GetField(name, BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        static int Commas(string sql) => sql[..sql.IndexOf("FROM", StringComparison.Ordinal)].Count(c => c == ',');

        Assert.Equal(Commas(Field("ColumnsSelect")), Commas(Field("SchemaColumnsSql")));
        Assert.DoesNotContain("data_default", Field("SchemaColumnsSql"), StringComparison.Ordinal);
    }

    private static ColumnInfo Col(string name, string type) => new(name, type, 20, false, null, null, true, false, null, 0);

    private static readonly TableDetails[] Tables =
    [
        new(new TableSummary("APP", "T", TableKind.Table),
            [Col("ID", "NUMBER"), Col("NAME", "VARCHAR2"), Col("D", "DATE"), Col("C", "CLOB"), Col("B", "BLOB"), Col("R", "RAW"), Col("X", "XMLTYPE")],
            ["ID"], [], false),
        new(new TableSummary("APP", "V", TableKind.View), [Col("NAME", "VARCHAR2"), Col("D", "DATE")], [], [], false),
    ];

    /// <summary>Every operator on every column kind, with values that look like SQL – they end up as binds only.</summary>
    [Fact]
    public void Query_builder_statements_are_plain_queries_whatever_the_filter()
    {
        var hostile = "x'; DELETE FROM t; --";
        foreach (var table in Tables)
        {
            foreach (var column in table.Columns)
            {
                foreach (var op in FilterRules.OperatorsFor(ColumnCategories.Of(column)))
                {
                    var value = ColumnCategories.Of(column) switch
                    {
                        ColumnCategory.Number => "1",
                        ColumnCategory.Date => "01.10.2026",
                        ColumnCategory.Raw => "CAFE",
                        _ => hostile,
                    };
                    var values = FilterRules.ValueCount(op) switch { 0 => Array.Empty<string>(), 2 => [value, value], _ => [value] };
                    var row = new FilterCondition(column.Name, op, values);
                    var other = FilterCondition.Of(table.Columns[0].Name, FilterOperator.IsNotNull);

                    // Alone, in an OR group and in a NOT group (WP-50, review 3.40.0 C5).
                    foreach (var filters in new[]
                    {
                        new[] { row },
                        [row with { Group = new FilterGroup(1, FilterGroupKind.Any) }, other with { Group = new FilterGroup(1, FilterGroupKind.Any) }],
                        [row with { Group = new FilterGroup(1, FilterGroupKind.None) }, other with { Group = new FilterGroup(1, FilterGroupKind.None) }, other],
                    })
                    {
                        var select = QueryBuilder.BuildSelect(table, filters, [new SortSpec(table.Columns[0].Name, true)], new PageSpec(0, 500));
                        var count = QueryBuilder.BuildCount(table, filters);
                        var star = QueryBuilder.BuildSelectStar(table, filters, [new SortSpec(table.Columns[0].Name, true)]);

                        Assert.True(OracleSession.IsReadOnlyStatement(select.Sql), select.Sql);
                        Assert.True(OracleSession.IsReadOnlyStatement(count.Sql), count.Sql);
                        Assert.True(OracleSession.IsReadOnlyStatement(star.Sql), star.Sql);
                        Assert.DoesNotContain("DELETE", select.Sql, StringComparison.OrdinalIgnoreCase);
                    }
                }
            }
        }
    }

    /// <summary>Review 3.40.0, C5: reading back a new row by its ROWID and reading a whole LOB are plain queries too.</summary>
    [Fact]
    public void The_statements_by_rowid_and_for_a_lob_are_plain_queries()
    {
        var table = Tables[0];
        var byRowId = QueryBuilder.BuildSelectByRowId(table, "AAAR3sAAEAAAACXAAA");
        Assert.True(OracleSession.IsReadOnlyStatement(byRowId.Sql), byRowId.Sql);

        foreach (var key in new RowKey[] { new RowKey.PrimaryKey([1m]), new RowKey.RowId("AAAR3sAAEAAAACXAAA") })
        {
            foreach (var lob in new[] { 3, 4 }) // CLOB, BLOB
            {
                var read = QueryBuilder.BuildSelectLob(table, key, lob);
                Assert.True(OracleSession.IsReadOnlyStatement(read.Sql), read.Sql);
            }
        }
    }

    [Fact]
    public void Display_lookups_are_plain_queries()
    {
        foreach (var table in Tables)
        {
            foreach (var size in QueryBuilder.LookupSizes)
            {
                var keys = Enumerable.Range(0, size).Select(_ => (object)"x'; DELETE FROM t; --").ToList();
                var lookup = QueryBuilder.BuildLookup(table, 0, table.Columns.Count - 1, keys);

                Assert.True(OracleSession.IsReadOnlyStatement(lookup.Sql), lookup.Sql);
            }
        }
    }
}
