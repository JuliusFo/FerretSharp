using FerretSharp.Core.Query;

namespace FerretSharp.Core.Tests.Query.SqlEditor;

/// <summary>Review WP-76: the table a DML statement writes and the columns it sets.</summary>
public sealed class SqlDmlTargetTests
{
    // Cases: EF Core's Oracle UPDATE; SET with a qualified column and a subquery as value (its columns are not set);
    // INSERT with a column list, and one without; MERGE with both branches; DELETE sets nothing; a CASE WHEN in a value
    // (EF Core's conditional SetProperty) and a column list set from a subquery (second pass of review WP-76).
    [Theory]
    [InlineData("""UPDATE "CUSTOMERS" SET "NAME" = :p0, "EMAIL" = :p1 WHERE "ID" = :p2""", "CUSTOMERS", "NAME, EMAIL")]
    [InlineData("UPDATE shop.orders o SET o.total = (SELECT SUM(p.amount) FROM positions p WHERE p.order_id = o.id) WHERE o.id = :id", "ORDERS", "TOTAL")]
    [InlineData("""INSERT INTO "ORDERS" ("ID", "CUSTOMER_ID") VALUES (:p0, :p1)""", "ORDERS", "ID, CUSTOMER_ID")]
    [InlineData("INSERT INTO orders SELECT * FROM orders_archive", "ORDERS", "")]
    [InlineData("MERGE INTO t USING s ON (t.id = s.id) WHEN MATCHED THEN UPDATE SET t.a = s.a WHEN NOT MATCHED THEN INSERT (id, b) VALUES (s.id, s.b)", "T", "A, ID, B")]
    [InlineData("""DELETE FROM "CUSTOMERS" WHERE "ID" = :p0""", "CUSTOMERS", "")]
    [InlineData("""UPDATE "ORDERS" "o" SET "STATUS" = CASE WHEN "o"."TOTAL" > 5 THEN N'BIG' ELSE "o"."STATUS" END, "NOTE" = :p0 WHERE "o"."ID" = :p1""", "ORDERS", "STATUS, NOTE")]
    [InlineData("UPDATE orders o SET (o.total, note) = (SELECT SUM(p.amount), 'x' FROM positions p WHERE p.order_id = o.id), status = 'D'", "ORDERS", "TOTAL, NOTE, STATUS")]
    public void The_target_is_the_first_table_and_the_columns_are_those_set(string sql, string table, string columns)
    {
        var target = SqlDmlTarget.Of(sql);

        Assert.NotNull(target);
        Assert.Equal(table, target.Table.Name);
        Assert.Equal(columns, string.Join(", ", target.Columns));
    }

    [Fact]
    public void A_query_has_no_target() => Assert.Null(SqlDmlTarget.Of("SELECT * FROM t WHERE t.a = 1"));
}
