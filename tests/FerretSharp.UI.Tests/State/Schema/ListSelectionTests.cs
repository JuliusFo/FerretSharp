using FerretSharp.UI.State;

namespace FerretSharp.UI.Tests.State;

/// <summary>Rows picked in the columns view by click, Ctrl+click, Shift+click and right-click.</summary>
public sealed class ListSelectionTests
{
    private static readonly IReadOnlyList<string> Shown = ["ID", "NAME", "PLZ", "ORT", "LAND"];

    private static IReadOnlyList<string> Picked(ListSelection selection) => selection.In(Shown);

    [Fact]
    public void A_click_picks_one_row_and_ctrl_adds_or_takes_away_one()
    {
        var selection = new ListSelection();

        selection.Click("NAME", Shown, ctrl: false, shift: false);
        selection.Click("ORT", Shown, ctrl: true, shift: false);
        Assert.Equal(["NAME", "ORT"], Picked(selection));

        selection.Click("NAME", Shown, ctrl: true, shift: false);
        Assert.Equal(["ORT"], Picked(selection));

        selection.Click("ID", Shown, ctrl: false, shift: false);
        Assert.Equal(["ID"], Picked(selection));
    }

    [Fact]
    public void Shift_picks_the_rows_between_the_last_click_and_this_one_in_the_order_shown()
    {
        var selection = new ListSelection();

        selection.Click("ORT", Shown, ctrl: false, shift: false);
        selection.Click("NAME", Shown, ctrl: false, shift: true);
        Assert.Equal(["NAME", "PLZ", "ORT"], Picked(selection));

        // From the same anchor again: the range replaces the one before.
        selection.Click("LAND", Shown, ctrl: false, shift: true);
        Assert.Equal(["ORT", "LAND"], Picked(selection));

        // With Ctrl the range is added.
        selection.Click("ID", Shown, ctrl: true, shift: false);
        selection.Click("NAME", Shown, ctrl: true, shift: true);
        Assert.Equal(["ID", "NAME", "ORT", "LAND"], Picked(selection));
    }

    [Fact]
    public void Shift_without_an_anchor_in_the_list_picks_the_row_alone()
    {
        var selection = new ListSelection();
        selection.Click("ORT", Shown, ctrl: false, shift: true);
        Assert.Equal(["ORT"], Picked(selection));

        // The anchor is filtered out: the click counts as a plain one.
        selection.Click("NAME", ["NAME", "PLZ"], ctrl: false, shift: true);
        Assert.Equal(["NAME"], Picked(selection));
    }

    [Fact]
    public void A_right_click_outside_the_picked_rows_picks_that_row_inside_them_keeps_them()
    {
        var selection = new ListSelection();
        selection.Click("NAME", Shown, ctrl: false, shift: false);
        selection.Click("ORT", Shown, ctrl: false, shift: true);

        selection.ContextClick("PLZ");
        Assert.Equal(["NAME", "PLZ", "ORT"], Picked(selection));

        selection.ContextClick("LAND");
        Assert.Equal(["LAND"], Picked(selection));
    }

    [Fact]
    public void Rows_a_filter_hides_are_not_among_the_picked_ones()
    {
        var selection = new ListSelection();
        selection.Click("ID", Shown, ctrl: false, shift: false);
        selection.Click("LAND", Shown, ctrl: false, shift: true);

        Assert.Equal(5, selection.Count);
        Assert.Equal(["PLZ", "ORT"], selection.In(["PLZ", "ORT"]));

        selection.Clear();
        Assert.Empty(Picked(selection));
    }
}
