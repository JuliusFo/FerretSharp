using FerretSharp.Core.Data;
using FerretSharp.Core.Oracle;
using FerretSharp.Core.Query;
using FerretSharp.Core.Schema;

namespace FerretSharp.Core.Tests.Query;

/// <summary>A query result sorted by a header click: ORDER BY by position, in the query or around it.</summary>
public sealed class SqlResultOrderTests
{
    private static readonly IReadOnlyList<SqlColumn> Distinct = [Column("ID"), Column("NAME"), Column("CITY")];

    private static readonly IReadOnlyList<SqlColumn> SameNames = [Column("ID"), Column("ID"), Column("NAME")];

    private static QuerySpec Query(string sql) => new(sql, [new QueryParameter("p", 1, OracleTypeHint.Number)]);

    private static string? Sorted(string sql, IReadOnlyList<SqlColumn> columns, params ResultSort[] sorts) =>
        SqlResultOrder.Sorted(Query(sql), columns, sorts)?.Sql;

    [Fact]
    public void Without_sorts_the_query_stays_as_it_is()
    {
        var query = Query("SELECT * FROM t");

        Assert.Same(query, SqlResultOrder.Sorted(query, Distinct, []));
    }

    [Fact]
    public void A_query_without_order_gets_one_by_position()
    {
        Assert.Equal("SELECT id, name FROM t WHERE id > :p\nORDER BY 2 DESC, 1",
            Sorted("SELECT id, name FROM t WHERE id > :p", SameNames, new(1, true), new(0, false)));
    }

    /// <summary>
    /// One key in front of the query's own order (which breaks ties, also from page to page), or appended by position: orders
    /// in parentheses, comments and literals are not the query's, a line comment at the end swallows nothing, a compound
    /// query is sorted as a whole, ROWNUM limits before ORDER BY (the same rows, re-sorted). Review 3.54.0, C14: seven facts
    /// of the same shape before.
    /// </summary>
    [Theory]
    [InlineData("SELECT id, name, city FROM t ORDER BY name DESC NULLS LAST", 2, false, "SELECT id, name, city FROM t ORDER BY 3, name DESC NULLS LAST")]
    [InlineData("WITH x AS (SELECT a FROM t ORDER BY a) SELECT a, ROW_NUMBER() OVER (ORDER BY a) rn, (SELECT MAX(b) FROM u) m FROM x", 0, false,
        "WITH x AS (SELECT a FROM t ORDER BY a) SELECT a, ROW_NUMBER() OVER (ORDER BY a) rn, (SELECT MAX(b) FROM u) m FROM x\nORDER BY 1")]
    [InlineData("SELECT id FROM t -- all of them", 0, true, "SELECT id FROM t -- all of them\nORDER BY 1 DESC")]
    [InlineData("SELECT 'ORDER BY x' t /* ORDER BY y */ FROM dual", 0, false, "SELECT 'ORDER BY x' t /* ORDER BY y */ FROM dual\nORDER BY 1")]
    [InlineData("SELECT a FROM t UNION ALL SELECT b FROM u", 0, false, "SELECT a FROM t UNION ALL SELECT b FROM u\nORDER BY 1")]
    [InlineData("SELECT id FROM t WHERE ROWNUM <= 10", 0, false, "SELECT id FROM t WHERE ROWNUM <= 10\nORDER BY 1")]
    public void One_key_goes_in_front_of_the_query_s_own_order_or_after_the_query(string sql, int column, bool descending, string expected) =>
        Assert.Equal(expected, Sorted(sql, SameNames, new ResultSort(column, descending)));

    /// <summary>The top 10 of the query's order, re-sorted – an order of its own would pick other rows.</summary>
    [Theory]
    [InlineData("SELECT id, name, city FROM t ORDER BY id DESC FETCH FIRST 10 ROWS ONLY")]
    [InlineData("SELECT id, name, city FROM t ORDER BY id OFFSET 20 ROWS")]
    [InlineData("SELECT id, name, city FROM t START WITH p IS NULL CONNECT BY PRIOR id = p ORDER SIBLINGS BY name")]
    public void A_query_that_limits_its_rows_is_wrapped(string sql)
    {
        Assert.Equal($"SELECT * FROM (\n{sql}\n) ORDER BY 2 DESC", Sorted(sql, Distinct, new ResultSort(1, true)));
    }

    /// <summary>ORA-00918: <c>SELECT *</c> over two columns of the same name – the database cannot sort it.</summary>
    [Fact]
    public void A_limited_query_with_columns_of_the_same_name_cannot_be_sorted()
    {
        var query = Query("SELECT a.id, b.id, a.name FROM a JOIN b ON b.a = a.id FETCH FIRST 5 ROWS ONLY");

        Assert.Null(SqlResultOrder.Sorted(query, SameNames, [new ResultSort(0, false)]));
        Assert.False(SqlResultOrder.CanSort(query, SameNames));
        Assert.True(SqlResultOrder.CanSort(query, Distinct));
    }

    [Fact]
    public void Binds_stay_and_the_sorted_query_passes_the_read_guard()
    {
        var sorted = SqlResultOrder.Sorted(Query("WITH x AS (SELECT :p a FROM dual) SELECT a FROM x FETCH FIRST 1 ROW ONLY"), Distinct, [new ResultSort(0, false)])!;
        var appended = SqlResultOrder.Sorted(Query("SELECT :p FROM dual"), Distinct, [new ResultSort(0, false)])!;

        Assert.Equal(Query("").Parameters, sorted.Parameters);
        Assert.True(OracleSession.IsReadOnlyStatement(sorted.Sql));
        Assert.True(OracleSession.IsReadOnlyStatement(appended.Sql));
    }

    private static SqlColumn Column(string name) => new(name, new ColumnInfo(name, "VARCHAR2", 100, true, null, null, false, false, null, 1));
}
