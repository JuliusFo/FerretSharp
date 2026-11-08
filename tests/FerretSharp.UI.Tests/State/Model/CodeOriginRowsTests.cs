using FerretSharp.Core.ClrModel;
using FerretSharp.UI.State;

namespace FerretSharp.UI.Tests.State;

/// <summary>WP-76: how the candidates for a statement's origin in code read.</summary>
public sealed class CodeOriginRowsTests
{
    private static CodeOriginCandidate Candidate(params CodeOriginReason[] reasons) => Candidate(3, Fit(1, 1), reasons);

    private static CodeOriginCandidate Candidate(int score, CodeOriginFit fit, params CodeOriginReason[] reasons) =>
        new("Shop.OrderService", "OpenOrders",
            new CodeUsage(new CodeUse("Shop.Order", "Status", CodeUseKind.Filter, "Shop.OrderService", "OpenOrders", "OrderService.cs", 12), "Shop", false, false, null),
            score, reasons, fit);

    private static CodeOriginFit Fit(int matched, int total) => new(matched, total, [], []);

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

    [Fact]
    public void Candidates_with_the_same_score_share_the_rank_of_the_first_and_are_level()
    {
        var candidates = new[] { 9, 7, 7, 4, 4 }.Select(score => Candidate(score, Fit(1, 1))).ToList();

        Assert.Equal([(1, false), (2, true), (2, true), (4, true), (4, true)], CodeOriginRows.Ranks(candidates));
    }

    [Theory]
    [InlineData(3, 3, 5, "full")]
    [InlineData(4, 5, 4, "half")] // never all five without a full fit
    [InlineData(1, 2, 3, "half")] // 2.5 rounds up
    [InlineData(1, 3, 2, "low")]
    [InlineData(1, 12, 1, "low")] // at least one: it always covers something
    public void The_meter_fills_by_the_share_covered(int matched, int total, int filled, string tone)
    {
        Assert.Equal(filled, CodeOriginRows.Filled(Fit(matched, total)));
        Assert.Equal(tone, CodeOriginRows.Tone(Fit(matched, total)));
    }

    [Fact]
    public void A_method_covering_all_but_filtering_beyond_the_statement_is_full_but_not_green()
    {
        var fit = new CodeOriginFit(2, 2, [], ["AUFTRAG.KUNDE_ID"]);

        Assert.Equal((CodeOriginRows.Segments, "half"), (CodeOriginRows.Filled(fit), CodeOriginRows.Tone(fit)));
    }

    [Fact]
    public void The_meter_s_tooltip_names_the_share_what_is_missing_and_what_the_method_has_beyond_it()
    {
        var fit = new CodeOriginFit(1, 4,
            [new CodeOriginReason(CodeOriginReasonKind.Queries, "CUSTOMERS", []), new CodeOriginReason(CodeOriginReasonKind.Filters, "ORDERS", ["STATUS"])],
            ["ORDERS.TOTAL", "Order.Customer"]);

        using (UiCulture.Use("en"))
        {
            Assert.Equal(
                "Matches 1 of 4 features of the statement\nMissing: queries CUSTOMERS · filters ORDERS by STATUS\nAlso in the method: ORDERS.TOTAL, Order.Customer",
                CodeOriginRows.FitTitle(fit, qualify: true));
        }

        Assert.Equal("Trifft 3 von 3 Merkmalen des Statements", CodeOriginRows.FitTitle(Fit(3, 3)));
    }
}
