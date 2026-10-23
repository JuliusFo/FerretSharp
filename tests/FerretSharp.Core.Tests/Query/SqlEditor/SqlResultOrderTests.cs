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

    /// <summary>The query's own order breaks ties: rows of the same city stay in it, also from page to page.</summary>
    [Fact]
    public void The_new_keys_go_in_front_of_the_query_s_own_order()
    {
        Assert.Equal("SELECT id, name, city FROM t ORDER BY 3, name DESC NULLS LAST",
            Sorted("SELECT id, name, city FROM t ORDER BY name DESC NULLS LAST", SameNames, new ResultSort(2, false)));
    }

    [Fact]
    public void Orders_inside_parentheses_are_not_the_query_s()
    {
        const string sql = "WITH x AS (SELECT a FROM t ORDER BY a) SELECT a, ROW_NUMBER() OVER (ORDER BY a) rn, (SELECT MAX(b) FROM u) m FROM x";

        Assert.Equal(sql + "\nORDER BY 1", Sorted(sql, SameNames, new ResultSort(0, false)));
    }

    [Fact]
    public void A_line_comment_at_the_end_does_not_swallow_the_order()
    {
        Assert.Equal("SELECT id FROM t -- all of them\nORDER BY 1 DESC", Sorted("SELECT id FROM t -- all of them", SameNames, new ResultSort(0, true)));
    }

    [Fact]
    public void Order_by_in_a_comment_or_a_literal_is_not_the_query_s()
    {
        Assert.Equal("SELECT 'ORDER BY x' t /* ORDER BY y */ FROM dual\nORDER BY 1",
            Sorted("SELECT 'ORDER BY x' t /* ORDER BY y */ FROM dual", SameNames, new ResultSort(0, false)));
    }

    [Fact]
    public void A_compound_query_is_sorted_as_a_whole()
    {
        Assert.Equal("SELECT a FROM t UNION ALL SELECT b FROM u\nORDER BY 1", Sorted("SELECT a FROM t UNION ALL SELECT b FROM u", SameNames, new ResultSort(0, false)));
    }

    /// <summary>ROWNUM limits before ORDER BY: the same rows, re-sorted.</summary>
    [Fact]
    public void A_rownum_limit_keeps_its_rows()
    {
        Assert.Equal("SELECT id FROM t WHERE ROWNUM <= 10\nORDER BY 1", Sorted("SELECT id FROM t WHERE ROWNUM <= 10", SameNames, new ResultSort(0, false)));
    }

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
