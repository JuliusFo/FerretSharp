using FerretSharp.Core.Query;
using FerretSharp.Core.Resources;
using FerretSharp.Core.Workspaces;
using FerretSharp.UI.Resources;
using FerretSharp.UI.State;

namespace FerretSharp.UI.Tests.State;

/// <summary>A table tab in SQL mode (WP-79): a condition written in SQL instead of the filter bar's rows, which wait meanwhile.</summary>
public class ConditionTabTests
{
    private static readonly FilterCondition[] ByName = [FilterCondition.Of("NAME", FilterOperator.Contains, "Meier")];

    private static readonly Core.Schema.TableDetails Details = new(
        TestApp.Kunden, [new Core.Schema.ColumnInfo("NAME", "VARCHAR2", 100, false, null, null, true, false, null, 1)], [], [], false);

    private static TableTab Tab() => new(Guid.NewGuid(), TestApp.Kunden) { AppliedFilters = ByName };

    /// <summary>
    /// The grid keeps the filter rows' result until a condition is applied, and gets it back when the tab goes back to the filter
    /// bar; only a text that is still the one the mode started with goes back without asking (decision of the user).
    /// </summary>
    [Fact]
    public void The_filter_rows_wait_while_a_condition_is_written_and_applied()
    {
        var tab = Tab();
        tab.SetFilterRows(ByName);

        tab.StartCondition("UPPER(NAME) LIKE UPPER('%Meier%') ESCAPE '\\'");
        Assert.Equal(RowFilter.Of(ByName), tab.ReadFilter);
        Assert.True(tab.SqlCondition!.Unchanged);

        tab.EditCondition("ID IN (SELECT KUNDE_ID FROM AUFTRAG)\n  AND ORT = 'Kiel'");
        Assert.False(tab.SqlCondition.Unchanged);
        Assert.Equal(RowFilter.Of(ByName), tab.ReadFilter); // not applied yet

        Assert.True(tab.ApplyCondition());
        Assert.Equal(RowFilter.Where("ID IN (SELECT KUNDE_ID FROM AUFTRAG)\n  AND ORT = 'Kiel'"), tab.ReadFilter);
        Assert.Equal((1, "ID IN (SELECT KUNDE_ID FROM AUFTRAG) AND ORT = 'Kiel'"), (tab.ActiveFilterCount, tab.FilterSummary(maxLength: 100)));

        tab.EndCondition();
        Assert.Equal(RowFilter.Of(ByName), tab.ReadFilter);
        Assert.Equal(["NAME"], tab.FilterRows.Select(r => r.Column));
    }

    [Fact]
    public void A_condition_that_cannot_stand_after_where_is_not_applied_and_says_why()
    {
        var tab = Tab();
        tab.StartCondition("");
        tab.EditCondition("ID = 1 ORDER BY NAME");

        Assert.False(tab.ApplyCondition());
        Assert.Equal(TextFormat.Format(QueryText.ConditionTopLevel, "ORDER"), tab.ConditionError);
        Assert.False(tab.ConditionApplied);

        tab.EditCondition("  ");
        Assert.True(tab.ApplyCondition()); // empty: every row
        Assert.Equal((RowFilter.None, 0, null), (tab.ReadFilter, tab.ActiveFilterCount, tab.ConditionError));
    }

    /// <summary>
    /// The statement shown under "SQL" is the one being written if it can stand after WHERE; a half-written one shows what the
    /// grid reads instead of failing.
    /// </summary>
    [Fact]
    public void The_statement_shown_is_the_condition_being_written_if_it_is_whole()
    {
        var tab = Tab();
        tab.StartCondition("");
        tab.EditCondition("ID > 3");
        Assert.Equal(RowFilter.Where("ID > 3"), tab.EditedFilter(Details));

        tab.EditCondition("ID > (3");
        Assert.Equal(RowFilter.Of(ByName), tab.EditedFilter(Details));
    }

    /// <summary>Saved with the workspace and back; a time travel tab never has SQL mode (decision of the user), nor gets it from a file.</summary>
    [Fact]
    public void The_condition_is_saved_and_a_time_travel_tab_has_none()
    {
        var tab = Tab();
        tab.StartCondition("");
        tab.EditCondition("ID > 3");
        tab.ApplyCondition();
        var state = tab.ToState([tab]);

        var restored = TableTab.Restore(Guid.NewGuid(), TestApp.Kunden, state);
        Assert.Equal(new SqlConditionState("ID > 3", "ID > 3", ""), restored.SqlCondition);

        var travel = TableTab.Restore(Guid.NewGuid(), TestApp.Kunden, state with { TimeTravel = new TimeTravelState(42, DateTimeOffset.Now, "DB") });
        Assert.Null(travel.SqlCondition);
        travel.StartCondition("");
        Assert.False(travel.SqlMode);

        var workspace = new Workspace(Guid.NewGuid(), Guid.NewGuid(), "W")
        {
            Tabs = [state with { TimeTravel = new TimeTravelState(42, DateTimeOffset.Now, "DB") }],
        }.Normalized();
        Assert.Null(workspace.Tabs[0].SqlCondition);
    }

    /// <summary>A link, a start point of the data diff and time travel take filter rows; with a condition applied they say why not.</summary>
    [Fact]
    public void What_needs_filter_rows_is_unavailable_with_a_condition_applied()
    {
        var tab = Tab();
        tab.StartCondition("");
        Assert.Null(WorkspaceLifecycle.DataDiffUnavailable(tab)); // nothing applied yet: the grid shows the filter rows

        tab.EditCondition("ID > 3");
        tab.ApplyCondition();

        Assert.Equal(GridText.Condition_NotWithCondition, WorkspaceLifecycle.DataDiffUnavailable(tab));
    }
}
