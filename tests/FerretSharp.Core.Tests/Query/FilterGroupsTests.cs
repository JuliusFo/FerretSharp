using FerretSharp.Core.Connections;
using FerretSharp.Core.Links;
using FerretSharp.Core.Query;
using FerretSharp.Core.Schema;
using FerretSharp.Core.Workspaces;

namespace FerretSharp.Core.Tests.Query;

/// <summary>The helpers of filter groups (WP-50): terms, active rows, new ids.</summary>
public class FilterGroupsTests
{
    private static readonly FilterGroup A = new(1, FilterGroupKind.Any);

    private static readonly FilterGroup B = new(2, FilterGroupKind.None);

    private static FilterCondition Row(string column, FilterGroup? group = null, bool enabled = true) =>
        new(column, FilterOperator.IsNull, [], enabled) { Group = group };

    [Fact]
    public void Terms_are_the_ungrouped_rows_and_the_groups_with_active_rows()
    {
        var terms = FilterGroups.Terms([Row("X"), Row("Y", A), Row("Z", B, enabled: false), Row("W", A), Row("V", enabled: false), Row("U")]);

        Assert.Equal([null, A, null], terms.Select(t => t.Group));
        Assert.Equal(["0", "1,3", "5"], terms.Select(t => string.Join(",", t.Indexes)));
    }

    [Fact]
    public void Active_rows_leave_out_disabled_rows_and_rows_of_disabled_groups()
    {
        var off = B with { Enabled = false };

        Assert.Equal(["X", "W"], FilterGroups.Active([Row("X"), Row("Y", off), Row("Z", A, enabled: false), Row("W", A)]).Select(f => f.Column));
    }

    [Fact]
    public void Renumbering_keeps_added_groups_apart_and_together()
    {
        var existing = new[] { Row("X", A), Row("Y", new FilterGroup(int.MaxValue, FilterGroupKind.Any)) };
        var added = FilterGroups.Renumber(existing, [Row("Z", A), Row("W"), Row("V", A), Row("U", B)]);

        // The smallest free ids: an id from outside (int.MaxValue) never overflows into another group.
        Assert.Equal([2, null, 2, 3], added.Select(f => f.Group?.Id));
        Assert.Equal(FilterGroupKind.None, added[3].Group!.Kind);
    }

    [Fact]
    public void Normalizing_numbers_the_groups_from_1_in_the_order_of_their_first_row()
    {
        var normalized = FilterGroups.Normalize([Row("X", B with { Id = 9 }), Row("Y"), Row("Z", A with { Id = int.MaxValue }), Row("W", B with { Id = 9, Enabled = false })]);

        Assert.Equal([1, null, 2, 1], normalized.Select(f => f.Group?.Id));
        Assert.Equal(normalized[0].Group, normalized[3].Group); // the first row's kind and switch
    }
}
