using System.Diagnostics;
using FerretSharp.Core.Query;

namespace FerretSharp.Core.Tests.Query.SqlEditor;

public sealed class SqlConditionsTests
{
    private static string Columns(string sql) =>
        string.Join(", ", SqlConditions.Of(sql).Select(c => c.Qualifier is null ? c.Column : $"{c.Qualifier}.{c.Column}"));

    // Cases: EF Core's Oracle shape (quoted, parenthesised, binds and literals, FETCH after); a function call and IS NULL;
    // IN with a list and BETWEEN; a join condition in WHERE and a correlated subquery (column = column: left out); the
    // select list of a subquery in the condition (no filter) while its own WHERE counts; ORDER BY ends the WHERE; an
    // unqualified column and keywords (kept; callers drop names that are no column); owner.table.column; no WHERE at all;
    // values without parentheses (USER, CURRENT_DATE) are no column, so the column compared with them is a filter; an outer
    // join in Oracle's old form (c.b(+)) is column = column (review WP-76).
    [Theory]
    [InlineData("""SELECT "o"."ID" FROM "ORDERS" "o" WHERE ("o"."CUSTOMER_ID" = :customerId_0) AND ("o"."STATUS" = N'OPEN') FETCH FIRST 1 ROWS ONLY""", "o.CUSTOMER_ID, o.STATUS")]
    [InlineData("select * from kunden k where upper(k.name) like :p and k.geloescht is null", "K.NAME, K.GELOESCHT")]
    [InlineData("SELECT * FROM t WHERE t.a IN (:a, :b, 3) AND t.b BETWEEN 1 AND :c", "T.A, T.B")]
    [InlineData("SELECT * FROM a, b WHERE a.id = b.a_id AND b.x = 1", "B.X")]
    [InlineData("""SELECT "c"."ID" FROM "CUSTOMERS" "c" WHERE EXISTS (SELECT 1 FROM "ORDERS" "o" WHERE "c"."ID" = "o"."CUSTOMER_ID" AND "o"."TOTAL" > :p0)""", "o.TOTAL")]
    [InlineData("SELECT * FROM t WHERE t.id IN (SELECT s.t_id FROM s WHERE s.flag = 'J')", "T.ID, S.FLAG")]
    [InlineData("SELECT * FROM t WHERE t.a = 1 ORDER BY t.b", "T.A")]
    [InlineData("SELECT * FROM t WHERE status = :s AND NOT deleted = 1", "STATUS, DELETED")]
    [InlineData("SELECT * FROM shop.orders WHERE shop.orders.total > 5", "ORDERS.TOTAL")]
    [InlineData("SELECT o.total FROM orders o ORDER BY o.total", "")]
    [InlineData("SELECT * FROM t WHERE t.owner = USER AND t.d > CURRENT_DATE", "T.OWNER, T.D")]
    [InlineData("SELECT * FROM a, c WHERE a.x = c.b(+) AND c.y = 1", "C.Y")]
    public void Columns_compared_in_a_where_of_any_level_count_joins_and_select_lists_do_not(string sql, string expected) =>
        Assert.Equal(expected, Columns(sql));

    [Fact]
    public void A_column_is_listed_once()
    {
        Assert.Equal("T.A", Columns("SELECT * FROM t WHERE t.a = 1 OR t.a = 2"));
    }

    /// <summary>
    /// Review WP-76: statement texts come from any session on the server, without a limit. Binds and columns were kept in
    /// lists searched for each new one, and every bind of an IN list walked the list back to its column: 65,000 binds
    /// (Oracle's limit, lists of 1,000) took seconds per analysis, on the render thread.
    /// </summary>
    [Fact]
    public void A_statement_with_tens_of_thousands_of_binds_or_columns_is_read_in_linear_time()
    {
        var lists = Enumerable.Range(0, 65).Select(l => "t.id IN (" + string.Join(", ", Enumerable.Range(0, 1000).Select(b => $":p{l * 1000 + b}")) + ")");
        var binds = "SELECT * FROM t WHERE " + string.Join(" OR ", lists);
        var columns = "SELECT * FROM t WHERE " + string.Join(" AND ", Enumerable.Range(0, 20_000).Select(c => $"t.c{c} = 1"));

        var watch = Stopwatch.StartNew();
        var info = SqlAnalysis.Analyze(binds);
        var found = SqlConditions.Of(columns);
        watch.Stop();

        Assert.Equal(65_000, info.Binds.Count);
        Assert.Equal(65_000, info.BindUses.Count(u => u.Column == "ID"));
        Assert.Equal(20_000, found.Count);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3), $"took {watch.Elapsed}");
    }
}
