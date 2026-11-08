using FerretSharp.Core.ClrModel;
using FerretSharp.UI.State;

namespace FerretSharp.UI.Tests.State;

/// <summary>WP-76: how the candidates for a statement's origin in code read.</summary>
public sealed class CodeOriginRowsTests
{
    private static CodeOriginCandidate Candidate(params CodeOriginReason[] reasons) =>
        new("Shop.OrderService", "OpenOrders",
            new CodeUsage(new CodeUse("Shop.Order", "Status", CodeUseKind.Filter, "Shop.OrderService", "OpenOrders", "OrderService.cs", 12), "Shop", false, false, null),
            3, reasons);

    [Fact]
    public void Reasons_read_as_one_line_each_with_its_table_navigation_or_columns()
    {
        var candidate = Candidate(
            new CodeOriginReason(CodeOriginReasonKind.Queries, "ORDERS", []),
            new CodeOriginReason(CodeOriginReasonKind.Joins, "CUSTOMERS", [], "Order.Customer"),
            new CodeOriginReason(CodeOriginReasonKind.LazyLoads, "CUSTOMERS", [], "Order.Customer"),
            new CodeOriginReason(CodeOriginReasonKind.Filters, "ORDERS", ["CUSTOMER_ID", "STATUS"]),
            new CodeOriginReason(CodeOriginReasonKind.Writes, "ORDERS", ["A", "B", "C", "D", "E"]));

        using (UiCulture.Use("en"))
        {
            Assert.Equal(
                "queries ORDERS · reads CUSTOMERS through Order.Customer · loads CUSTOMERS on access to Order.Customer (lazy loading)"
                + " · filters by CUSTOMER_ID, STATUS · writes A, B, C, D, … of ORDERS",
                CodeOriginRows.Reasons(candidate));
        }

        Assert.Equal("fragt ORDERS ab", CodeOriginRows.Reason(candidate.Reasons[0]));
        // Several tables in the statement: the filter names its table (review WP-76).
        Assert.Equal("filtert ORDERS nach CUSTOMER_ID, STATUS", CodeOriginRows.Reason(candidate.Reasons[3], qualify: true));
    }

    [Fact]
    public void The_first_five_are_shown_all_when_expanded()
    {
        var candidates = Enumerable.Range(0, 7).Select(_ => Candidate()).ToList();

        Assert.Equal(CodeOriginRows.Initial, CodeOriginRows.Shown(candidates, expanded: false).Count);
        Assert.Equal(7, CodeOriginRows.Shown(candidates, expanded: true).Count);
    }
}
