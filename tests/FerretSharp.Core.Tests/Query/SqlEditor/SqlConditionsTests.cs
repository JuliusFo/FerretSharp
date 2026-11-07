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
    // a filter after a GROUP BY at the outer level comes back in a later WHERE only.
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
    public void Columns_compared_in_a_where_of_any_level_count_joins_and_select_lists_do_not(string sql, string expected) =>
        Assert.Equal(expected, Columns(sql));

    [Fact]
    public void A_column_is_listed_once()
    {
        Assert.Equal("T.A", Columns("SELECT * FROM t WHERE t.a = 1 OR t.a = 2"));
    }
}
