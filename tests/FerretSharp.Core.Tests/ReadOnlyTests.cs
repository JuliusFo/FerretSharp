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

    private static readonly string[] WritingNames = ["NonQuery", "Scalar", "Insert", "Update", "Delete", "Merge", "Flush", "Write"];

    private static readonly string[] TransactionControlNames = ["Commit", "Rollback", "Savepoint", "BeginTransaction"];

    private static List<string> PublicMethods(Type type) =>
        type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Select(m => m.Name)
            .ToList();

    /// <summary>The database entry points: no public method may write data (until WP-09 brings FlushAsync).</summary>
    [Theory]
    [InlineData(typeof(OracleSession))]
    [InlineData(typeof(IDataAccess))]
    [InlineData(typeof(ISchemaReader))]
    [InlineData(typeof(IDatabaseConnection))]
    [InlineData(typeof(IDatabaseConnector))]
    public void Database_types_offer_no_writing_methods(Type type) =>
        Assert.DoesNotContain(PublicMethods(type), name => WritingNames.Any(w => name.Contains(w, StringComparison.OrdinalIgnoreCase)));

    /// <summary>Commit, rollback and savepoints exist on the session only, not on what the UI gets to see.</summary>
    [Theory]
    [InlineData(typeof(IDataAccess))]
    [InlineData(typeof(ISchemaReader))]
    [InlineData(typeof(IDatabaseConnection))]
    [InlineData(typeof(IDatabaseConnector))]
    public void Transaction_control_is_not_exposed_beyond_the_session(Type type) =>
        Assert.DoesNotContain(PublicMethods(type), name => TransactionControlNames.Any(w => name.Contains(w, StringComparison.OrdinalIgnoreCase)));

    [Fact]
    public void The_write_path_is_internal()
    {
        var method = typeof(OracleSession).GetMethod("ExecuteNonQueryAsync", BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(method);
        Assert.True(method.IsAssembly);
    }

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

    [Fact]
    public void The_schema_path_is_internal()
    {
        var method = typeof(OracleSession).GetMethod("ExecuteDdlAsync", BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(method);
        Assert.True(method.IsAssembly);
    }

    /// <summary>WP-22, ADR 0019: the schema path takes DDL the SQL editor runs – a single statement, nothing else.</summary>
    [Theory]
    [InlineData("CREATE TABLE t (id NUMBER PRIMARY KEY, name VARCHAR2(40 CHAR))")]
    [InlineData("  alter table t add (c number default 0 not null)")]
    [InlineData("-- schema\nDROP TABLE t PURGE")]
    [InlineData("COMMENT ON COLUMN t.c IS 'a;b'")]
    [InlineData("CREATE OR REPLACE VIEW v AS SELECT * FROM t")]
    [InlineData("CREATE INDEX t_c ON t (c);")]
    [InlineData("GRANT SELECT ON t TO x")]
    [InlineData("ALTER TRIGGER t_bi DISABLE")]
    public void Schema_path_accepts_single_ddl(string sql) => Assert.True(OracleSession.IsDdlStatement(sql), sql);

    [Theory]
    [InlineData("TRUNCATE TABLE t")]
    [InlineData("ALTER SESSION SET CURRENT_SCHEMA = X")]
    [InlineData("ALTER SYSTEM KILL SESSION '1,2'")]
    [InlineData("CREATE OR REPLACE PROCEDURE p AS BEGIN NULL; END;")]
    [InlineData("CREATE TRIGGER t_bi BEFORE INSERT ON t BEGIN NULL; END;")]
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
                    var filters = new[] { new FilterCondition(column.Name, op, values) };

                    var select = QueryBuilder.BuildSelect(table, filters, [new SortSpec(table.Columns[0].Name, true)], new PageSpec(0, 500));
                    var count = QueryBuilder.BuildCount(table, filters);

                    Assert.True(OracleSession.IsReadOnlyStatement(select.Sql), select.Sql);
                    Assert.True(OracleSession.IsReadOnlyStatement(count.Sql), count.Sql);
                    Assert.DoesNotContain("DELETE", select.Sql, StringComparison.OrdinalIgnoreCase);
                }
            }
        }
    }
}
