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

/// <summary>Filter groups in links and presets (WP-50).</summary>
public class FilterGroupTransferTests
{
    private static ColumnInfo Column(string name, string type, int position) =>
        new(name, type, type == "VARCHAR2" ? 50 : null, false, null, null, true, IsIdentity: false, Default: null, Position: position);

    private static readonly TableDetails Auftrag = new(new TableSummary("ERP", "AUFTRAG", TableKind.Table),
        [Column("ID", "NUMBER", 1), Column("STATUS", "VARCHAR2", 2), Column("KBEZ", "VARCHAR2", 3)], ["ID"], [], false);

    private static readonly ConnectionProfile Profile = new(Guid.NewGuid(), "ERP Test", ConnectionKind.Test, new HostPortAddress("db01", 1521, "ERP", null), "erp", null, false);

    private static readonly FilterGroup Any = new(3, FilterGroupKind.Any);

    [Fact]
    public void A_link_with_groups_is_version_2_and_reads_back_its_groups()
    {
        var link = TableLink.For(Profile, Auftrag.Table.Ref, [
            FilterCondition.Of("ID", FilterOperator.Gt, "5"),
            FilterCondition.Of("KBEZ", FilterOperator.Equals, "NA") with { Group = Any },
            FilterCondition.Of("STATUS", FilterOperator.In, "OFFEN", "NEU") with { Group = Any },
            FilterCondition.Of("STATUS", FilterOperator.IsNull) with { Group = new FilterGroup(4, FilterGroupKind.None, Enabled: false) },
        ], [], TabMode.Data);

        var uri = link.ToUri();
        var read = TableLink.Parse(uri).Link!;

        Assert.StartsWith("ferretsharp://open?v=2&", uri);
        Assert.Contains("&f=ID,gt,5&g=1,any,KBEZ,equals,NA&g=1,any,STATUS,in,OFFEN,NEU", uri, StringComparison.Ordinal);
        Assert.Equal(3, read.Filters.Count); // the disabled group filters nothing and is left out
        Assert.Equal([null, Any with { Id = 1 }, Any with { Id = 1 }], read.Filters.Select(f => f.Group)); // numbered from 1
        Assert.Equal(["OFFEN", "NEU"], read.Filters[2].Values);
    }

    [Fact]
    public void A_link_without_groups_stays_version_1_for_older_ferretsharp()
    {
        var link = TableLink.For(Profile, Auftrag.Table.Ref, [FilterCondition.Of("ID", FilterOperator.Gt, "5")], [], TabMode.Data);

        Assert.StartsWith("ferretsharp://open?v=1&", link.ToUri());
    }

    [Fact]
    public void An_appended_preset_brings_its_groups_with_new_ids()
    {
        var current = new[] { FilterCondition.Of("ID", FilterOperator.Gt, "5") with { Group = Any } };
        var preset = new FilterPreset(Guid.NewGuid(), "NA oder offen", "AUFTRAG",
            [FilterCondition.Of("KBEZ", FilterOperator.Equals, "NA") with { Group = Any }, FilterCondition.Of("STATUS", FilterOperator.IsNull) with { Group = Any }], []);

        var applied = FilterPresets.Apply(preset, Auftrag, current, append: true);

        Assert.Equal([1, 2, 2], applied.Filters.Select(f => f.Group!.Id));
        Assert.False(FilterPresets.Differs(preset, Auftrag, preset.Filters, []));
        Assert.True(FilterPresets.Differs(preset, Auftrag, [preset.Filters[0], preset.Filters[1] with { Group = null }], []));
        Assert.True(FilterPresets.Differs(preset, Auftrag, [.. preset.Filters.Select(f => f with { Group = Any with { Kind = FilterGroupKind.None } })], []));
        // The filter bar numbers groups anew: other ids alone are no change.
        Assert.False(FilterPresets.Differs(preset, Auftrag, [.. preset.Filters.Select(f => f with { Group = Any with { Id = 1 } })], []));
    }

    [Fact]
    public void Group_ids_of_a_link_start_at_1_and_larger_ones_are_refused()
    {
        var link = TableLink.For(Profile, Auftrag.Table.Ref, [FilterCondition.Of("KBEZ", FilterOperator.IsNull) with { Group = Any with { Id = int.MaxValue } }], [], TabMode.Data);

        Assert.Contains("&g=1,any,KBEZ,isNull", link.ToUri(), StringComparison.Ordinal);
        Assert.Contains("ungültigen Filter auf", TableLink.Parse("ferretsharp://open?v=2&s=ERP&t=X&g=51,any,KBEZ,isNull").Error);
        Assert.Contains("ungültigen Filter auf abc", TableLink.Parse("ferretsharp://open?v=2&s=ERP&t=X&g=abc").Error);
    }
}
