using FerretSharp.Core.Data;
using FerretSharp.Core.Query;
using FerretSharp.Core.Schema;
using FerretSharp.UI.State;

namespace FerretSharp.UI.Tests.State;

/// <summary>R6b (review 3.40.0, B5): the jump lists and row texts both right-click menus share.</summary>
public class FkJumpListsTests
{
    private static readonly TableRef Kunde = new("APP", "KUNDE");

    private static FkJump Jump(string name, JumpDirection direction) =>
        new(new ForeignKeyInfo(name, new("APP", "AUFTRAG"), ["KUNDE_ID"], Kunde, ["ID"], FkSource.Declared), direction, Kunde, [], null);

    private static RowData Row(object? value) => new(RowKey.None.Instance, [value]);

    private static GridContextMenuRequest Request(IReadOnlyList<RowData> selected, int missing = 0) =>
        new(Row(1m), 0, "1", selected, missing, 0, 0, 1000, 800);

    [Fact]
    public void Jumps_are_split_by_direction_with_those_over_the_clicked_column_first_in_their_order()
    {
        var a = Jump("A", JumpDirection.Outgoing);
        var b = Jump("B", JumpDirection.Incoming);
        var c = Jump("C", JumpDirection.Outgoing);
        var d = Jump("D", JumpDirection.Incoming);
        var e = Jump("E", JumpDirection.Outgoing);

        var lists = FkJumpLists.Of([a, b, c, d, e], j => j == c || j == e || j == d);

        Assert.Equal([c, e, a], lists.Outgoing);
        Assert.Equal([d, b], lists.Incoming);
        Assert.Equal(5, lists.Count);
        Assert.Equal(0, FkJumpLists.None.Count);
    }

    [Fact]
    public void Jumps_through_base_tables_follow_the_direct_ones_grouped_by_base_table_with_related_first()
    {
        var basis = new TableRef("APP", "BASIS");
        var oben = new TableRef("APP", "OBEN");
        FkJump Through(string name, TableRef via) =>
            Jump(name, JumpDirection.Incoming) with { Inherited = new(Kunde, new ForeignKeyInfo(name, Kunde, ["ID"], via, ["ID"], FkSource.Declared), via, ["ID"]) };
        var direct = Jump("D", JumpDirection.Incoming);
        var m1 = Through("M1", basis);
        var o1 = Through("O1", oben);
        var m2 = Through("M2", basis);

        var lists = FkJumpLists.Of([m1, o1, direct, m2], j => j == m2);

        Assert.Equal([direct, m2, m1, o1], lists.Incoming);
        var groups = FkJumpLists.Groups(lists.Incoming);
        Assert.Equal([null, basis, oben], groups.Select(g => g.Via));
        Assert.Equal([[direct], [m2, m1], [o1]], groups.Select(g => g.Jumps));
        Assert.Equal(3, lists.Headings);
        Assert.Equal(2, FkJumpLists.Of([Jump("A", JumpDirection.Outgoing), direct], _ => false).Headings);
    }

    [Fact]
    public void Jumps_start_from_the_selected_rows_else_from_the_clicked_one()
    {
        var selected = new[] { Row(2m), Row(3m) };

        Assert.Equal(selected, Request(selected).JumpRows);
        Assert.Equal(1m, Assert.Single(Request([]).JumpRows).Values[0]);
    }

    [Fact]
    public void The_row_count_is_named_only_for_more_than_one_row_counting_those_no_longer_loaded()
    {
        Assert.Equal("", Request([Row(2m)]).JumpRowsText);
        Assert.Equal("", Request([]).JumpRowsText);
        Assert.Equal(" · 2 Zeilen", Request([Row(2m), Row(3m)]).JumpRowsText);
        Assert.Equal(" · 1 Zeile", Request([Row(2m)], missing: 1).JumpRowsText);
    }

    private static FkJump NullJump(string name) => Jump(name, JumpDirection.Outgoing) with { Unavailable = "KUNDE_ID ist NULL.", SkippedRows = 1 };

    [Fact]
    public void Two_or_more_null_jumps_fold_into_one_line_but_not_one_over_the_clicked_column()
    {
        var a = Jump("A", JumpDirection.Outgoing);
        var n1 = NullJump("N1");
        var n2 = NullJump("N2");
        var mine = NullJump("MINE");

        var (shown, folded) = FkJumpLists.FoldNull([mine, a, n1, n2], j => j == mine);

        Assert.Equal([mine, a], shown);
        Assert.Equal([n1, n2], folded);
        Assert.Empty(FkJumpLists.FoldNull([a, n1, mine], j => j == mine).Folded);
    }

    [Fact]
    public void A_long_incoming_list_shows_the_first_eight_but_one_more_does_not_fold()
    {
        var nine = Enumerable.Range(0, 9).Select(i => Jump($"F{i}", JumpDirection.Incoming)).ToList();
        var ten = Enumerable.Range(0, 10).Select(i => Jump($"F{i}", JumpDirection.Incoming)).ToList();

        Assert.Empty(FkJumpLists.FoldIncoming(nine).Folded);
        var (shown, folded) = FkJumpLists.FoldIncoming(ten);
        Assert.Equal(ten.Take(8), shown);
        Assert.Equal(ten.Skip(8), folded);
    }
}
