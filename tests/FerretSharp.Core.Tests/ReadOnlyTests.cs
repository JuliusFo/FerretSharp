using System.Reflection;
using FerretSharp.Core.Connections;
using FerretSharp.Core.Data;
using FerretSharp.Core.Oracle;
using FerretSharp.Core.Query;
using FerretSharp.Core.Schema;

namespace FerretSharp.Core.Tests;

/// <summary>
/// v1 is read-only (CLAUDE.md, section 2). These tests keep it that way: the session refuses anything but plain
/// queries, no type offers a writing method, and every statement FerretSharp builds passes the guard.
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
    public void Everything_else_is_refused(string sql) => Assert.False(OracleSession.IsReadOnlyStatement(sql), sql);

    private static readonly string[] WritingNames =
        ["NonQuery", "Scalar", "Transaction", "Commit", "Rollback", "Savepoint", "Insert", "Update", "Delete", "Merge", "Flush", "Write"];

    /// <summary>The only database entry points: no method on them may sound like writing.</summary>
    [Theory]
    [InlineData(typeof(OracleSession))]
    [InlineData(typeof(IDataAccess))]
    [InlineData(typeof(ISchemaReader))]
    [InlineData(typeof(IDatabaseConnection))]
    [InlineData(typeof(IDatabaseConnector))]
    public void Database_types_offer_no_writing_methods(Type type)
    {
        var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Select(m => m.Name)
            .ToList();

        Assert.DoesNotContain(methods, name => WritingNames.Any(w => name.Contains(w, StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void Session_runs_statements_only_through_the_guarded_reader()
    {
        var executing = typeof(OracleSession).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.Name.StartsWith("Execute", StringComparison.Ordinal))
            .Select(m => m.Name);

        Assert.Equal(["ExecuteReaderAsync"], executing);
    }

    /// <summary>All SQL text constants of the Oracle layer (data dictionary queries) must pass the guard.</summary>
    [Fact]
    public void Schema_reader_statements_are_plain_queries()
    {
        var statements = typeof(OracleSchemaReader).GetFields(BindingFlags.NonPublic | BindingFlags.Static)
            .Where(f => f.FieldType == typeof(string) && f.Name.EndsWith("Sql", StringComparison.Ordinal))
            .Select(f => (f.Name, Sql: (string)f.GetValue(null)!))
            .ToList();

        Assert.NotEmpty(statements);
        Assert.All(statements, s => Assert.True(OracleSession.IsReadOnlyStatement(s.Sql), s.Name));
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
