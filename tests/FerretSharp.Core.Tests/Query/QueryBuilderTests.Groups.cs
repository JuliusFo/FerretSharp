using FerretSharp.Core.Connections;
using FerretSharp.Core.Links;
using FerretSharp.Core.Query;
using FerretSharp.Core.Schema;
using FerretSharp.Core.Workspaces;

namespace FerretSharp.Core.Tests.Query;

/// <summary>Filter groups in the SQL (WP-50): OR and NOT with C#'s meaning of NULL.</summary>
public partial class QueryBuilderTests
{
    private static readonly FilterGroup AnyGroup = new(1, FilterGroupKind.Any);

    private static readonly FilterGroup NoneGroup = new(2, FilterGroupKind.None);

    private static FilterCondition In(FilterGroup group, string column, FilterOperator op, params string[] values) =>
        FilterCondition.Of(column, op, values) with { Group = group };

    [Fact]
    public void An_or_group_is_one_term_beside_the_other_rows()
    {
        var (where, parameters) = Where(
            FilterCondition.Of("KUNDE_ID", FilterOperator.Gt, "5"),
            In(AnyGroup, "KUERZEL", FilterOperator.Equals, "AB"),
            In(AnyGroup, "NAME", FilterOperator.Contains, "x"));

        Assert.Equal("t.\"KUNDE_ID\" > :p0\n   AND (t.\"KUERZEL\" = :p1 OR UPPER(t.\"NAME\") LIKE UPPER(:p2) ESCAPE '\\')", where);
        Assert.Equal(3, parameters.Count);
    }

    [Fact]
    public void A_group_of_one_row_is_that_row()
    {
        Assert.Equal("t.\"KUERZEL\" = :p0", Where(In(AnyGroup, "KUERZEL", FilterOperator.Equals, "AB")).Where);
        Assert.Equal("CASE WHEN t.\"KUERZEL\" = :p0 THEN 1 ELSE 0 END = 0", Where(In(NoneGroup, "KUERZEL", FilterOperator.Equals, "AB")).Where);
    }

    [Fact]
    public void A_not_group_keeps_the_rows_whose_conditions_are_unknown_as_csharp_does()
    {
        var (where, _) = Where(
            In(NoneGroup, "KUERZEL", FilterOperator.Equals, "AB"),
            In(NoneGroup, "ERSTELLT_AM", FilterOperator.Gte, "1.10.2026"));

        // NOT (KUERZEL = 'AB') would lose the rows where KUERZEL is NULL; C#'s !(x.Kuerzel == "AB") keeps them.
        Assert.Equal("CASE WHEN t.\"KUERZEL\" = :p0 OR t.\"ERSTELLT_AM\" >= :p1 THEN 1 ELSE 0 END = 0", where);
    }

    [Fact]
    public void A_not_group_whose_sql_is_never_unknown_is_a_plain_not()
    {
        var (where, _) = Where(
            In(NoneGroup, "KUERZEL", FilterOperator.IsNull),
            In(NoneGroup, "ERSTELLT_AM", FilterOperator.NotEquals, "1.10.2026")); // "≠" on a nullable column says OR … IS NULL

        Assert.Equal(
            "NOT (t.\"KUERZEL\" IS NULL OR ((t.\"ERSTELLT_AM\" < :p0 OR t.\"ERSTELLT_AM\" >= :p1) OR t.\"ERSTELLT_AM\" IS NULL))",
            where);
    }

    [Theory]
    [InlineData(FilterOperator.StartsWith)]
    [InlineData(FilterOperator.Equals)]
    [InlineData(FilterOperator.NotEquals)]
    public void A_not_null_column_does_not_make_a_plain_not(FilterOperator op)
    {
        // The dictionary's NOT NULL can be wrong: a view over an outer join, a constraint ENABLE NOVALIDATE.
        var (where, _) = Where(In(NoneGroup, "NAME", op, "A"));

        Assert.StartsWith("CASE WHEN ", where, StringComparison.Ordinal);
    }

    [Fact]
    public void A_day_range_stays_one_term_in_an_or_group()
    {
        var (where, _) = Where(
            In(AnyGroup, "KUERZEL", FilterOperator.IsNull),
            In(AnyGroup, "ERSTELLT_AM", FilterOperator.Between, "1.10.2026", "31.10.2026"));

        Assert.Equal("(t.\"KUERZEL\" IS NULL OR (t.\"ERSTELLT_AM\" >= :p0 AND t.\"ERSTELLT_AM\" < :p1))", where);
    }

    [Fact]
    public void Groups_combine_with_and_in_the_order_of_their_first_row()
    {
        var other = new FilterGroup(7, FilterGroupKind.Any);
        var (where, _) = Where(
            In(AnyGroup, "KUERZEL", FilterOperator.IsNull),
            In(other, "NAME", FilterOperator.Equals, "a"),
            FilterCondition.Of("KUNDE_ID", FilterOperator.Equals, "1"),
            In(AnyGroup, "ERSTELLT_AM", FilterOperator.IsNull),
            In(other, "NAME", FilterOperator.Equals, "b"));

        Assert.Equal(
            "(t.\"KUERZEL\" IS NULL OR t.\"ERSTELLT_AM\" IS NULL)\n   AND (t.\"NAME\" = :p0 OR t.\"NAME\" = :p1)\n   AND t.\"KUNDE_ID\" = :p2",
            where);
    }

    [Fact]
    public void A_disabled_group_filters_nothing_and_is_not_checked()
    {
        var off = AnyGroup with { Enabled = false };
        var filters = new[] { In(off, "KUNDE_ID", FilterOperator.Equals, "keine Zahl"), In(off, "KUERZEL", FilterOperator.IsNull) };

        Assert.Empty(QueryBuilder.Validate(Kunden, filters));
        Assert.Equal("", Where(filters).Where);
    }

    [Fact]
    public void A_group_without_an_enabled_row_filters_nothing()
    {
        var (where, _) = Where(
            FilterCondition.Of("KUERZEL", FilterOperator.IsNull),
            In(NoneGroup, "NAME", FilterOperator.Equals, "a") with { Enabled = false });

        Assert.Equal("t.\"KUERZEL\" IS NULL", where);
    }

    [Fact]
    public void Errors_of_group_rows_are_keyed_by_their_index()
    {
        var errors = QueryBuilder.Validate(Kunden, [
            FilterCondition.Of("KUERZEL", FilterOperator.IsNull),
            In(AnyGroup, "KUNDE_ID", FilterOperator.Equals, "keine Zahl"),
        ]);

        Assert.Equal([1], errors.Keys);
    }

    [Fact]
    public void A_group_takes_kind_and_switch_from_its_first_row()
    {
        // A list read from outside may disagree: the first row decides.
        var (where, _) = Where(
            In(AnyGroup, "KUERZEL", FilterOperator.IsNull),
            In(AnyGroup with { Kind = FilterGroupKind.None, Enabled = false }, "ERSTELLT_AM", FilterOperator.IsNull));

        Assert.Equal("(t.\"KUERZEL\" IS NULL OR t.\"ERSTELLT_AM\" IS NULL)", where);
    }
}
