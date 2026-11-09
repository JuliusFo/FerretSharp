using FerretSharp.Core.Connections;
using FerretSharp.Core.Data;
using FerretSharp.Core.Query;
using FerretSharp.Core.Schema;
using FerretSharp.Testing;

namespace FerretSharp.Core.Tests.Data;

/// <summary>Pairing the rows of a data diff (WP-44).</summary>
public sealed class DataDiffTests
{
    private static ColumnInfo Col(string name, string type) => new(name, type, null, false, null, null, true, false, null, 0);

    private static readonly TableDetails Orders = new(
        new TableSummary("APP", "ORDERS", TableKind.Table), [Col("ID", "NUMBER"), Col("STATUS", "VARCHAR2"), Col("NOTE", "CLOB"), Col("DOC", "XMLTYPE")],
        ["ID"], [], false);

    private static DiffSideRow Row(bool now, decimal id, string? status, object? note = null) =>
        new(now, new RowData(new RowKey.PrimaryKey([id]), [id, status, note, null]));

    private static DiffRead Read(bool more, params DiffSideRow[] rows) => new(rows, more, new HashSet<int> { 3 }, RowKeyKind.PrimaryKey, RowMovement: false);

    [Fact]
    public void A_key_on_one_side_is_added_or_deleted_on_both_changed_with_the_cells_that_differ()
    {
        var diff = DataDiff.Pair(Orders, [], Read(false,
            Row(true, 1, "new"),
            Row(false, 2, "open", new LobValue("abc", 3)), Row(true, 2, "done", new LobValue("abc", 4)),
            Row(false, 3, "gone")));

        Assert.Equal([DiffStatus.Added, DiffStatus.Changed, DiffStatus.Deleted], diff.Rows.Select(r => r.Status));
        Assert.Equal([1, 2], diff.Rows[1].Changed.Order()); // STATUS and the CLOB's length; never the omitted XMLTYPE
        Assert.Equal("open", diff.Rows[1].Then!.Values[1]);
        Assert.Equal("done", diff.Rows[1].Shown.Values[1]);
        Assert.Equal("gone", diff.Rows[2].Shown.Values[1]); // a deleted row shows its values at the start
        Assert.False(diff.Truncated);
        Assert.Equal((1, 1, 1), (diff.Count(DiffStatus.Added), diff.Count(DiffStatus.Changed), diff.Count(DiffStatus.Deleted)));
    }

    [Fact]
    public void A_cut_read_leaves_out_its_last_key_which_may_miss_its_other_half()
    {
        var diff = DataDiff.Pair(Orders, [], Read(true, Row(true, 1, "a"), Row(false, 2, "b")));

        Assert.Equal([1m], diff.Rows.Select(r => ((RowKey.PrimaryKey)r.Key).Values[0]));
        Assert.True(diff.Truncated);
    }

    /// <summary>Review of WP-44: a workspace file edited by hand with a table twice broke the tab (its list is keyed by table).</summary>
    [Fact]
    public void A_start_point_from_the_file_keeps_each_table_once_and_none_without_a_scn()
    {
        var table = new TableRef("APP", "ORDERS");
        var state = new Core.Workspaces.DataDiffState(4711, DateTimeOffset.UnixEpoch,
            [new(table, [new FilterCondition("ID", FilterOperator.Equals, ["1"])]), new(table, []), null!, new(new TableRef("APP", "LOG"), null!)]);

        var normalized = state.Normalized()!;

        Assert.Equal([table, new TableRef("APP", "LOG")], normalized.Entries.Select(e => e.Table));
        Assert.Single(normalized.Entries[0].Filters);
        Assert.Empty(normalized.Entries[1].Filters);
        Assert.Null((state with { Scn = 0 }).Normalized());
    }

    [Fact]
    public void Errors_the_user_can_act_on_are_explained()
    {
        var table = new TableRef("OTHER", "ORDERS");
        var at = new DateTimeOffset(2026, 11, 8, 14, 32, 0, TimeSpan.Zero);
        using (UiCulture.Use("en"))
        {
            Assert.Contains("GRANT FLASHBACK ON \"OTHER\".\"ORDERS\"", DataDiffErrors.Explain(new DatabaseException("x", "ORA-01031"), table, at), StringComparison.Ordinal);
            Assert.Contains("undo", DataDiffErrors.Explain(new DatabaseException("x", "ORA-01555"), table, at), StringComparison.Ordinal);
            Assert.Contains("undo", DataDiffErrors.Explain(new DatabaseException("x", "ORA-08181"), table, at), StringComparison.Ordinal);
            Assert.Contains("ORA-01466", DataDiffErrors.Explain(new DatabaseException("x", "ORA-01466"), table, at), StringComparison.Ordinal);
        }

        Assert.Null(DataDiffErrors.Explain(new DatabaseException("x", "ORA-00942"), table, at));
    }
}
