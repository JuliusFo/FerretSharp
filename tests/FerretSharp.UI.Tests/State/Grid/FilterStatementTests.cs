using FerretSharp.Core.Query;
using FerretSharp.Core.Schema;
using FerretSharp.Core.Settings;
using FerretSharp.UI.State;

namespace FerretSharp.UI.Tests.State;

/// <summary>"SQL" in the filter bar: the grid's statement or <c>SELECT t.*</c>, as the setting says.</summary>
public sealed class FilterStatementTests
{
    private static readonly TableDetails Kunden = new(new TableSummary("APP", "KUNDEN", TableKind.Table),
        [
            new("KUNDE_ID", "NUMBER", null, false, 10, 0, false, false, null, 1),
            new("NAME", "VARCHAR2", 100, false, null, null, true, false, null, 2),
        ],
        ["KUNDE_ID"], [], true);

    private static readonly IReadOnlyList<FilterCondition> Filters = [new("NAME", FilterOperator.Contains, ["Ferret"])];

    private static readonly IReadOnlyList<SortSpec> Sorts = [new("NAME", false)];

    [Fact]
    public void Listed_is_the_grids_statement_for_its_first_page()
    {
        var page = QueryBuilder.BuildSelect(Kunden, Filters, Sorts, new PageSpec(0, 500));

        var shown = FilterStatement.Of(Kunden, Filters, Sorts, FilterSqlColumns.Listed);

        Assert.Equal(page.Sql, shown.Sql);
        Assert.Equal(page.Parameters, shown.Parameters);
    }

    [Fact]
    public void Star_is_select_star_with_the_same_filter()
    {
        var shown = FilterStatement.Of(Kunden, Filters, Sorts, FilterSqlColumns.Star);

        var star = QueryBuilder.BuildSelectStar(Kunden, Filters, Sorts);
        Assert.Equal(star.Sql, shown.Sql);
        Assert.Equal(star.Parameters, shown.Parameters);
        Assert.StartsWith(QueryBuilder.StarSelect + "\n", shown.Sql);
    }

    [Fact]
    public void An_invalid_filter_is_refused_in_both_forms()
    {
        IReadOnlyList<FilterCondition> invalid = [new("GIBT_ES_NICHT", FilterOperator.Equals, ["1"])];

        Assert.Throws<QueryValidationException>(() => FilterStatement.Of(Kunden, invalid, [], FilterSqlColumns.Listed));
        Assert.Throws<QueryValidationException>(() => FilterStatement.Of(Kunden, invalid, [], FilterSqlColumns.Star));
    }
}
