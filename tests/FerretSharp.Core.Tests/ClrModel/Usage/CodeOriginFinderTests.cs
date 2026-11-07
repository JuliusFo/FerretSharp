using FerretSharp.Core.ClrModel;

namespace FerretSharp.Core.Tests.ClrModel.Usage;

/// <summary>Candidates for a statement over a hand-made model and uses, as the IL scanner would find them (WP-76).</summary>
public sealed class CodeOriginFinderTests
{
    private const string Ns = "Shop.";

    private static PropertyExport Property(string name, string column) => CodeUsageIndexTests.Property(name, column);

    private static ModelExport Model() => new(ModelExport.CurrentFormatVersion, "8.0.0", Ns + "ShopContext", "options", null,
    [
        new EntityExport("Customer", Ns + "Customer", false, null, "CUSTOMERS", null, null, null,
            [Property("Id", "ID"), Property("Name", "NAME"), Property("Email", "EMAIL")], ["Id"], [], "Customers"),
        new EntityExport("Order", Ns + "Order", false, null, "ORDERS", null, null, null,
            [Property("Id", "ID"), Property("CustomerId", "CUSTOMER_ID"), Property("Status", "STATUS"), Property("Total", "TOTAL")], ["Id"],
            [new ForeignKeyExport(["CustomerId"], "Customer", ["Id"], "Customer", "Orders", false)], "Orders"),
        new EntityExport("Report", Ns + "Report", false, null, null, null, "ORDER_REPORT", "REPORTING",
            [Property("Status", "STATUS")], [], []),
    ]);

    private static CodeUse Use(string type, string member, CodeUseKind kind, string caller, int line, string file = "OrderService.cs") =>
        new(Ns + type, member, kind, Ns + "OrderService", caller, file, line);

    /// <summary>
    /// OpenOrders: <c>db.Orders.Where(o =&gt; o.CustomerId == id &amp;&amp; o.Status == "OPEN")</c>; ByTotal:
    /// <c>db.Set&lt;Order&gt;().Where(o =&gt; o.Total &gt; x)</c>; WithCustomer: <c>db.Orders.Include(o =&gt; o.Customer)</c>;
    /// Lazy: <c>order.Customer.Name</c> outside a query; Rename: sets <c>Customer.Name</c>; a test and the context's DbSet
    /// property query orders too.
    /// </summary>
    private static CodeUsageIndex Index(params CodeUse[] extra)
    {
        CodeUse[] uses =
        [
            Use("ShopContext", "Orders", CodeUseKind.Read, "OpenOrders", 12),
            Use("Order", "CustomerId", CodeUseKind.Filter, "OpenOrders", 13),
            Use("Order", "Status", CodeUseKind.Filter, "OpenOrders", 13),
            Use("Order", "", CodeUseKind.Query, "ByTotal", 20),
            Use("Order", "Total", CodeUseKind.Filter, "ByTotal", 21),
            Use("ShopContext", "Orders", CodeUseKind.Read, "WithCustomer", 30),
            Use("Order", "Customer", CodeUseKind.Expression, "WithCustomer", 31),
            Use("Order", "Customer", CodeUseKind.Read, "Lazy", 40),
            Use("Customer", "Name", CodeUseKind.Read, "Lazy", 40),
            Use("Customer", "Name", CodeUseKind.Write, "Rename", 50),
            .. extra,
        ];
        var app = new AssemblyUses(AssemblyUses.CurrentFormatVersion, Guid.NewGuid(), "Shop", ["Shop", "Shop.Tests"], false, true, [], uses);
        var tests = new AssemblyUses(AssemblyUses.CurrentFormatVersion, Guid.NewGuid(), "Shop.Tests", ["Shop", "Shop.Tests"], true, true, [],
            [Use("ShopContext", "Orders", CodeUseKind.Read, "Test", 5, "OrderTests.cs") with { CallerType = "Shop.Tests.OrderTests" }]);
        return CodeUsageIndex.Build(Model(), [app, tests]);
    }

    private static List<string> Methods(CodeOriginResult result) => result.Candidates.Select(c => c.CallerMethod).ToList();

    [Fact]
    public void The_method_filtering_by_the_same_columns_comes_first_others_on_the_table_follow()
    {
        var result = CodeOriginFinder.Find(
            """SELECT "o"."ID", "o"."TOTAL" FROM "ORDERS" "o" WHERE ("o"."CUSTOMER_ID" = :id_0) AND ("o"."STATUS" = N'OPEN')""", "SHOP", Index());

        Assert.Equal(["ORDERS"], result.Tables);
        Assert.Equal(["OpenOrders", "ByTotal", "WithCustomer"], Methods(result)); // not the test; ByTotal and WithCustomer filter or join what the statement does not
        var best = result.Candidates[0];
        Assert.Equal(12, best.Use.Use.Line); // the first matching use: where the query starts
        Assert.Equal([CodeOriginReasonKind.Queries, CodeOriginReasonKind.Filters], best.Reasons.Select(r => r.Kind).ToList());
        Assert.Equal(["CUSTOMER_ID", "STATUS"], best.Reasons[1].Columns);
    }

    [Fact]
    public void A_join_over_a_navigation_beats_a_query_of_one_table_only()
    {
        const string sql = """SELECT "o"."ID", "c"."NAME" FROM "ORDERS" "o" INNER JOIN "CUSTOMERS" "c" ON "o"."CUSTOMER_ID" = "c"."ID" """;

        var result = CodeOriginFinder.Find(sql, null, Index());

        Assert.Equal(["ORDERS", "CUSTOMERS"], result.Tables);
        Assert.Equal(result.Tables, CodeOriginFinder.TablesOf(sql, null, Model()));
        var best = result.Candidates[0];
        Assert.Equal("WithCustomer", best.CallerMethod);
        Assert.Equal((CodeOriginReasonKind.Joins, "CUSTOMERS", "Order.Customer"), (best.Reasons[1].Kind, best.Reasons[1].Table, best.Reasons[1].Navigation));
    }

    [Fact]
    public void A_lazy_load_by_key_points_to_the_navigation_read_outside_a_query()
    {
        var result = CodeOriginFinder.Find("""SELECT "c"."ID", "c"."NAME" FROM "CUSTOMERS" "c" WHERE "c"."ID" = :p_0""", null, Index());

        var best = result.Candidates[0];
        Assert.Equal("Lazy", best.CallerMethod);
        Assert.Equal(CodeOriginReasonKind.LazyLoads, best.Reasons[0].Kind);
        Assert.Equal(3, best.Score); // navigation 2 + key-only filter 1: above the join (2)
        Assert.Contains(result.Candidates, c => c.CallerMethod == "WithCustomer" && c.Score == 2);
    }

    [Fact]
    public void Dml_counts_the_methods_writing_the_entity_a_query_does_not()
    {
        var index = Index();

        var update = CodeOriginFinder.Find("""UPDATE "CUSTOMERS" SET "NAME" = :p0 WHERE "ID" = :p1""", null, index);
        var query = CodeOriginFinder.Find("""SELECT "c"."NAME" FROM "CUSTOMERS" "c" WHERE "c"."EMAIL" = :p0""", null, index);

        var rename = Assert.Single(update.Candidates, c => c.CallerMethod == "Rename");
        var writes = Assert.Single(rename.Reasons);
        Assert.Equal((CodeOriginReasonKind.Writes, "CUSTOMERS"), (writes.Kind, writes.Table));
        Assert.Equal(["NAME"], writes.Columns);
        Assert.DoesNotContain(query.Candidates, c => c.CallerMethod == "Rename");
    }

    [Fact]
    public void A_filter_alone_is_a_query_of_the_entity_for_a_generic_repository()
    {
        // repository.Find(o => o.Total > x): the lambda in the caller, Set<T>() in generic code the index cannot name.
        var index = Index(Use("Order", "Total", CodeUseKind.Filter, "BigOrders", 60));

        var result = CodeOriginFinder.Find("""SELECT * FROM "ORDERS" "o" WHERE "o"."TOTAL" > :x""", null, index);

        var methods = Methods(result);
        Assert.Equal("ByTotal", methods[0]);
        Assert.True(methods.IndexOf("BigOrders") < methods.IndexOf("OpenOrders")); // OpenOrders filters by other columns
    }

    [Fact]
    public void Tables_filters_and_navigations_the_statement_and_the_method_do_not_share_cost_it()
    {
        var result = CodeOriginFinder.Find(
            """SELECT * FROM "ORDERS" "o" JOIN "CUSTOMERS" "c" ON "c"."ID" = "o"."CUSTOMER_ID" JOIN "INVOICES" "i" ON 1 = 1""", null,
            Index(Use("Order", "", CodeUseKind.Query, "AllOrders", 80)));

        Assert.Equal(["ORDERS", "CUSTOMERS"], result.Tables); // INVOICES is no entity's
        // AllOrders: ORDERS 3, CUSTOMERS untouched −1; ByTotal also filters by TOTAL, which the statement does not: −1 more.
        Assert.Equal(2, Assert.Single(result.Candidates, c => c.CallerMethod == "AllOrders").Score);
        Assert.Equal(1, Assert.Single(result.Candidates, c => c.CallerMethod == "ByTotal").Score);
        Assert.DoesNotContain(result.Candidates, c => c.CallerMethod == "OpenOrders"); // 3 − 1 − 2 filters
    }

    [Theory]
    [InlineData("SELECT * FROM V$SESSION WHERE SID = :1", "SYS")]               // no entity's table
    [InlineData("""SELECT * FROM "OTHER"."ORDERS" "o" """, "SHOP")]             // same name in another schema … only if the entity names one
    [InlineData("""SELECT * FROM "ORDER_REPORT" "r" """, "SHOP")]               // the view's entity has schema REPORTING
    [InlineData("BEGIN pkg.run; END;", "SHOP")]                                  // PL/SQL: no statement of EF's
    [InlineData("""SELECT * FROM "ORDERS"@remote "o" """, "SHOP")]              // another database's table
    public void A_statement_without_a_table_of_the_model_has_no_tables_and_no_candidates(string sql, string schema)
    {
        var model = Model();
        var orders = model.Entities[1] with { Schema = "SHOP" };
        var index = CodeUsageIndex.Build(model with { Entities = [model.Entities[0], orders, model.Entities[2]] }, []);

        Assert.Equal(CodeOriginResult.None, CodeOriginFinder.Find(sql, schema, index));
        Assert.Empty(CodeOriginFinder.TablesOf(sql, schema, index.Model));
    }

    [Fact]
    public void A_view_matches_with_its_schema_and_its_columns()
    {
        var index = Index(Use("Report", "Status", CodeUseKind.Filter, "Report", 70));

        var result = CodeOriginFinder.Find("""SELECT * FROM "ORDER_REPORT" "r" WHERE "r"."STATUS" = :s""", "REPORTING", index);

        var report = Assert.Single(result.Candidates);
        Assert.Equal(["STATUS"], Assert.Single(report.Reasons).Columns);
    }
}
