using FerretSharp.Core.Query;
using FerretSharp.Core.Workspaces;
using FerretSharp.UI.State;

namespace FerretSharp.UI.Tests.State;

/// <summary>Filter groups in the filter bar of a tab (WP-50): order of the rows, moving, ending a group, texts.</summary>
public sealed class FilterGroupRowsTests
{
    private static readonly Guid Workspace = Guid.NewGuid();

    private static TableTab Tab() => new(Workspace, TestApp.Kunden);

    private static FilterRow Row(string column) => new() { Column = column, Op = FilterOperator.IsNull };

    private static string Columns(TableTab tab) => string.Join(",", tab.FilterRows.Select(r => r.Column));

    [Fact]
    public void Rows_stand_without_a_group_first_then_group_by_group()
    {
        var tab = Tab();
        var or = tab.AddFilterGroup(FilterGroupKind.Any);
        var not = tab.AddFilterGroup(FilterGroupKind.None);
        tab.AddFilterRow(Row("A"), not);
        tab.AddFilterRow(Row("B"), or);
        tab.AddFilterRow(Row("C"));
        tab.AddFilterRow(Row("D"), or);

        Assert.Equal("C,B,D,A", Columns(tab));
        Assert.Equal([1, 2], tab.FilterGroups.Select(g => g.Id));
        Assert.Equal([null, 1, 1, 2], tab.EditedFilters().Select(f => f.Group?.Id));
        Assert.Equal(FilterGroupKind.None, tab.EditedFilters()[3].Group!.Kind);
    }

    [Fact]
    public void A_moved_row_goes_to_the_end_of_its_new_group()
    {
        var tab = Tab();
        var or = tab.AddFilterGroup(FilterGroupKind.Any);
        var a = Row("A");
        tab.AddFilterRow(a);
        tab.AddFilterRow(Row("B"));
        tab.AddFilterRow(Row("C"), or);

        tab.MoveFilterRow(a, or);
        Assert.Equal("B,C,A", Columns(tab));

        tab.MoveFilterRow(a, null);
        Assert.Equal("B,A,C", Columns(tab));
        Assert.Null(a.Group);
    }

    [Fact]
    public void Ending_a_group_keeps_its_rows_combined_with_and()
    {
        var tab = Tab();
        var or = tab.AddFilterGroup(FilterGroupKind.Any);
        tab.AddFilterRow(Row("A"), or);
        tab.AddFilterRow(Row("B"));

        tab.DissolveFilterGroup(or);

        Assert.Empty(tab.FilterGroups);
        Assert.Equal("B,A", Columns(tab));
        Assert.All(tab.EditedFilters(), f => Assert.Null(f.Group));
    }

    [Fact]
    public void Groups_survive_saving_and_restoring_the_tab()
    {
        var tab = Tab();
        var not = tab.AddFilterGroup(FilterGroupKind.None);
        not.Enabled = false;
        tab.AddFilterRow(Row("A"), not);
        tab.AddFilterRow(Row("B"), not);
        tab.AddFilterRow(Row("C"));

        var restored = TableTab.Restore(Workspace, TestApp.Kunden, tab.ToState([tab]));

        Assert.Equal("C,A,B", Columns(restored));
        var group = Assert.Single(restored.FilterGroups);
        Assert.Equal((1, FilterGroupKind.None, false), (group.Id, group.Kind, group.Enabled));
        Assert.Same(restored.FilterRows[1].Group, restored.FilterRows[2].Group);
    }

    [Fact]
    public void The_summary_shows_groups_as_one_text_each()
    {
        var any = new FilterGroup(1, FilterGroupKind.Any);
        var none = new FilterGroup(2, FilterGroupKind.None);
        var tab = Tab();
        tab.AppliedFilters =
        [
            FilterCondition.Of("KUNDE_ID", FilterOperator.Equals, "1"),
            FilterCondition.Of("NAME", FilterOperator.Equals, "a") with { Group = any },
            FilterCondition.Of("NAME", FilterOperator.Equals, "b") with { Group = any },
            FilterCondition.Of("ORT", FilterOperator.IsNull) with { Group = none },
            FilterCondition.Of("ORT", FilterOperator.Equals, "x") with { Group = none with { Id = 3, Enabled = false } },
        ];

        Assert.Equal(["KUNDE_ID = 1", "(NAME = a | NAME = b)", "¬(ORT ist NULL)"], tab.FilterTexts());
        Assert.Equal(4, tab.ActiveFilterCount);
    }

    [Fact]
    public void Preset_lines_name_the_group_of_a_row()
    {
        var any = new FilterGroup(5, FilterGroupKind.Any, Enabled: false);
        var lines = FilterPresetText.FilterLines(
        [
            FilterCondition.Of("KUNDE_ID", FilterOperator.Equals, "1"),
            FilterCondition.Of("NAME", FilterOperator.Equals, "a") with { Group = any },
        ]);

        Assert.Equal(["KUNDE_ID = 1", "(aus) ODER-Gruppe 1: NAME = a"], lines);
    }
}
