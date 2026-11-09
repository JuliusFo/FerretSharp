using FerretSharp.Core.Query;

namespace FerretSharp.Core.Tests.Query;

// The filter bar's filter as a SQL condition to go on with (WP-79): what the grid's WHERE says, with literals and plain names.
public partial class QueryBuilderTests
{
    public static TheoryData<FilterCondition[], string> ConditionTexts => new()
    {
        { [], "" },
        { [FilterCondition.Of("KUNDE_ID", FilterOperator.Equals, "4711")], "KUNDE_ID = 4711" },
        // Quotes doubled; "≠" on a nullable column keeps its NULL rows, as in the grid.
        { [FilterCondition.Of("KUERZEL", FilterOperator.NotEquals, "O'K")], "(KUERZEL <> 'O''K' OR KUERZEL IS NULL)" },
        { [FilterCondition.Of("Mixed Case", FilterOperator.IsNull)], "\"Mixed Case\" IS NULL" },
        { [FilterCondition.Of("NAME", FilterOperator.Contains, "50%")], "UPPER(NAME) LIKE UPPER('%50\\%%') ESCAPE '\\'" },
        { [FilterCondition.Of("KUNDE_ID", FilterOperator.In, "1", "2,5")], "KUNDE_ID IN (1, 2.5)" },
        // A date without time is the whole day.
        {
            [FilterCondition.Of("ERSTELLT_AM", FilterOperator.Equals, "1.10.2026")],
            "(ERSTELLT_AM >= TO_DATE('2026-10-01 00:00:00', 'YYYY-MM-DD HH24:MI:SS') AND ERSTELLT_AM < TO_DATE('2026-10-02 00:00:00', 'YYYY-MM-DD HH24:MI:SS'))"
        },
        // Each row an AND term; a disabled row filters nothing and is left out; a group keeps its parentheses.
        {
            [
                FilterCondition.Of("KUNDE_ID", FilterOperator.Gt, "3"),
                FilterCondition.Of("NAME", FilterOperator.Equals, "x") with { Enabled = false },
                FilterCondition.Of("NAME", FilterOperator.StartsWith, "A") with { Group = new FilterGroup(1, FilterGroupKind.Any) },
                FilterCondition.Of("NAME", FilterOperator.StartsWith, "B") with { Group = new FilterGroup(1, FilterGroupKind.Any) },
            ],
            "KUNDE_ID > 3\nAND (UPPER(NAME) LIKE UPPER('A%') ESCAPE '\\' OR UPPER(NAME) LIKE UPPER('B%') ESCAPE '\\')"
        },
    };

    [Theory]
    [MemberData(nameof(ConditionTexts))]
    public void The_filter_as_a_condition_has_literals_and_plain_names(FilterCondition[] filters, string expected)
    {
        var text = QueryBuilder.ConditionText(Kunden, filters);

        Assert.Equal(expected, text);
        Assert.True(text.Length == 0 || WhereCondition.Problem(text) is null, text); // what it writes, it takes back
    }

    [Fact]
    public void A_filter_that_cannot_be_applied_gives_no_condition() =>
        Assert.Throws<QueryValidationException>(() => QueryBuilder.ConditionText(Kunden, [FilterCondition.Of("KUNDE_ID", FilterOperator.Equals, "abc")]));
}
