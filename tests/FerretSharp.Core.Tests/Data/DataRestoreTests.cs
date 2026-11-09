using FerretSharp.Core.Connections;
using FerretSharp.Core.Data;
using FerretSharp.Core.Query;
using FerretSharp.Core.Schema;
using NSubstitute;

namespace FerretSharp.Core.Tests.Data;

/// <summary>Setting rows of a data diff back to the start point as pending changes (WP-78, ADR 0048), and the rules for points in time.</summary>
public sealed class DataRestoreTests
{
    private static ColumnInfo Col(string name, string type, bool identity = false, bool always = false, bool isVirtual = false) =>
        new(name, type, null, false, null, null, true, identity, null, 0, IsVirtual: isVirtual, IdentityAlways: always);

    private static readonly TableDetails Orders = new(
        new TableSummary("APP", "ORDERS", TableKind.Table),
        [Col("ID", "NUMBER", identity: true), Col("STATUS", "VARCHAR2"), Col("NOTE", "CLOB"), Col("TOTAL", "NUMBER", isVirtual: true), Col("LEGACY", "LONG")],
        ["ID"], [], false);

    private static RowData Row(decimal id, string? status, object? note = null, object? legacy = null) =>
        new(new RowKey.PrimaryKey([id]), [id, status, note, 99m, legacy]);

    [Fact]
    public void A_deleted_row_comes_back_as_an_insert_a_changed_one_as_an_update_of_its_old_values_an_added_one_as_a_delete()
    {
        var tracker = new ChangeTracker(Orders);

        var result = tracker.Restore([
            new RestoreRow(DiffStatus.Deleted, Row(1, "open", "a long text"), null),
            new RestoreRow(DiffStatus.Changed, Row(2, "open", "old"), Row(2, "done", "new")),
            new RestoreRow(DiffStatus.Added, null, Row(3, "new")),
        ]);

        Assert.Equal((3, 0, 0, 3), (result.Restored, result.AlreadyChanged, result.NotRestorable, result.Changes.Count));
        var operations = tracker.PendingOperations();
        var insert = operations.Single(o => o.Kind == OperationKind.Insert);
        // The identity BY DEFAULT keeps its value, the virtual column is left out, so are NULLs (as a new row of the grid).
        Assert.Equal(new Dictionary<int, object?> { [0] = 1m, [1] = "open", [2] = "a long text" }, insert.Values);
        var update = operations.Single(o => o.Kind == OperationKind.Update);
        Assert.Equal(new Dictionary<int, object?> { [1] = "open", [2] = "old" }, update.Values);
        Assert.Equal("done", update.Expected[1]); // checked against what the workspace reads now
        Assert.Equal(new RowKey.PrimaryKey([3m]), operations.Single(o => o.Kind == OperationKind.Delete).Key);

        tracker.UndoEdit(); // one step: Ctrl+Z takes all of it back
        Assert.False(tracker.HasChanges);
    }

    [Fact]
    public void Rows_the_tab_has_changes_of_and_values_that_cannot_be_written_back_are_left_alone()
    {
        var tracker = new ChangeTracker(Orders);
        tracker.SetValue(Row(2, "done"), 1, "typed");

        var result = tracker.Restore([
            new RestoreRow(DiffStatus.Changed, Row(2, "open"), Row(2, "done")),
            new RestoreRow(DiffStatus.Deleted, Row(4, "open", legacy: new NotNullMarker("LONG")), null), // a LONG is not read, so not written back
            new RestoreRow(DiffStatus.Deleted, Row(5, "open"), null),
        ]);

        Assert.Equal((1, 1, 1), (result.Restored, result.AlreadyChanged, result.NotRestorable));
        Assert.Equal("typed", tracker.Find(new RowKey.PrimaryKey([2m]))!.ValueOf(1)); // the user's edit stays
    }

    /// <summary>
    /// Review of WP-78: values the grid never writes came back changed – TIMESTAMP(9) without its nanoseconds, a time zone region as
    /// a fixed offset, INTERVAL as text; a NOT NULL column added since the start point read NULL then, its UPDATE failed with
    /// ORA-01407; a deleted row whose key a new row of the tab has already collided (ORA-00001).
    /// </summary>
    [Theory]
    [InlineData("TIMESTAMP(9)", 9)]
    [InlineData("TIMESTAMP(6) WITH TIME ZONE", 6)]
    [InlineData("INTERVAL DAY(2) TO SECOND(6)", 6)]
    [InlineData("BINARY_DOUBLE", null)]
    public void A_value_that_cannot_come_back_exactly_leaves_its_row_alone(string type, int? scale)
    {
        var table = new TableDetails(Orders.Table,
            [Col("ID", "NUMBER"), Col("NAME", "VARCHAR2"), new ColumnInfo("AT", type, null, false, null, scale, true, false, null, 0)], ["ID"], [], false);
        RowData Values(decimal id, string name, object? at) => new(new RowKey.PrimaryKey([id]), [id, name, at]);
        var tracker = new ChangeTracker(table);

        var result = tracker.Restore([
            new RestoreRow(DiffStatus.Deleted, Values(1, "old", "a value"), null),
            new RestoreRow(DiffStatus.Changed, Values(2, "old", "a value"), Values(2, "new", "another")),
            new RestoreRow(DiffStatus.Changed, Values(3, "old", "same"), Values(3, "new", "same")), // not written: comes back
        ]);

        Assert.Equal((1, 2), (result.Restored, result.NotRestorable));
        Assert.Equal([1], tracker.PendingOperations().Single().Values.Keys);
    }

    /// <summary>
    /// Second review of WP-78: two TIMESTAMP(9) values that differ only in their nanoseconds read alike – the diff saw a change,
    /// the row came back with nothing pending and without a word.
    /// </summary>
    [Fact]
    public void A_changed_row_that_reads_alike_with_fractions_beyond_dotnet_is_counted_not_dropped()
    {
        var table = new TableDetails(Orders.Table, [Col("ID", "NUMBER"), new ColumnInfo("AT", "TIMESTAMP(9)", null, false, null, 9, true, false, null, 0)],
            ["ID"], [], false);
        var at = new DateTime(2026, 11, 9, 9, 30, 0);
        var row = new RowData(new RowKey.PrimaryKey([1m]), [1m, at]);

        var result = new ChangeTracker(table).Restore([new RestoreRow(DiffStatus.Changed, row, row)]);

        Assert.Equal((0, 1), (result.Restored, result.NotRestorable));
    }

    [Fact]
    public void A_not_null_column_reading_null_then_is_left_out_and_a_deleted_row_whose_key_a_new_row_has_stays_away()
    {
        var table = Orders with
        {
            Columns = [.. Orders.Columns.Select(c => c.Name switch { "STATUS" => c with { Nullable = false }, "ID" => c with { IsIdentity = false }, _ => c })],
        };
        var tracker = new ChangeTracker(table);
        var added = tracker.AddRow();
        tracker.SetValue(added, 0, "7");

        var result = tracker.Restore([
            new RestoreRow(DiffStatus.Changed, Row(2, null, "old"), Row(2, "added since", "new")), // STATUS added since: NULL then
            new RestoreRow(DiffStatus.Deleted, Row(7, "gone"), null),
        ]);

        Assert.Equal((1, 1), (result.Restored, result.AlreadyChanged));
        Assert.Equal([2], tracker.PendingOperations().Single(o => o.Kind == OperationKind.Update).Values.Keys); // only NOTE
    }

    [Fact]
    public async Task Rows_with_a_lob_beyond_what_is_loaded_whole_are_left_out_unread()
    {
        var then = Substitute.For<IDataAccess>();
        var now = Substitute.For<IDataAccess>();
        var huge = new DiffRow(DiffStatus.Deleted, new RowKey.PrimaryKey([1m]),
            new RowData(new RowKey.PrimaryKey([1m]), [1m, "x", new LobValue("abc", LobLimits.MaxLoadLength + 1), 0m, null]), null, new HashSet<int>());

        var read = await DataRestore.ReadAsync(then, now, Orders, [huge], TestContext.Current.CancellationToken);

        Assert.Equal((0, 1), (read.Rows.Count, read.TooLarge));
        await then.DidNotReceive().ReadByKeysAsync(Arg.Any<TableDetails>(), Arg.Any<IReadOnlyList<int>?>(), Arg.Any<IReadOnlyList<IReadOnlyList<object>>>(),
            Arg.Any<bool>(), Arg.Any<int?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Deleted_rows_of_a_table_with_an_identity_generated_always_are_refused()
    {
        var always = Orders with { Columns = [Col("ID", "NUMBER", identity: true, always: true), .. Orders.Columns.Skip(1)] };
        var tracker = new ChangeTracker(always);

        Assert.NotNull(ChangeTracker.RestoreUnavailable(always, deletedRows: true));
        Assert.Throws<RefusedException>(() => tracker.Restore([new RestoreRow(DiffStatus.Deleted, Row(1, "open"), null)]));

        // A changed row keeps its id: setting its other values back works, the identity is never written.
        var result = tracker.Restore([new RestoreRow(DiffStatus.Changed, Row(2, "open"), Row(2, "done"))]);
        Assert.Equal(1, result.Restored);
        Assert.Equal([1], tracker.PendingOperations().Single().Values.Keys);
    }

    [Fact]
    public async Task The_rows_are_read_whole_then_as_of_the_start_and_now_in_the_workspace_and_those_changed_since_are_counted()
    {
        var then = Substitute.For<IDataAccess>();
        var now = Substitute.For<IDataAccess>();
        then.ReadByKeysAsync(Orders, Arg.Any<IReadOnlyList<int>?>(), Arg.Any<IReadOnlyList<IReadOnlyList<object>>>(), true, null, Arg.Any<CancellationToken>())
            .Returns([Row(1, "open", "whole text"), Row(2, "open"), Row(6, "old")]);
        now.ReadByKeysAsync(Orders, Arg.Any<IReadOnlyList<int>?>(), Arg.Any<IReadOnlyList<IReadOnlyList<object>>>(), true, null, Arg.Any<CancellationToken>())
            .Returns([Row(2, "done"), Row(3, "new"), Row(6, "inserted again")]);
        DiffRow Diff(DiffStatus status, decimal id) => new(status, new RowKey.PrimaryKey([id]), null, null, new HashSet<int>());

        var read = await DataRestore.ReadAsync(then, now, Orders,
            [Diff(DiffStatus.Deleted, 1), Diff(DiffStatus.Changed, 2), Diff(DiffStatus.Added, 3), Diff(DiffStatus.Added, 5), Diff(DiffStatus.Deleted, 6)],
            TestContext.Current.CancellationToken);

        Assert.Equal([1m, 2m, 3m], read.Rows.Select(r => ((RowKey.PrimaryKey)(r.Now ?? r.Then)!.Key).Values[0]));
        Assert.Equal("whole text", read.Rows[0].Then!.Values[2]);
        Assert.Equal(2, read.Gone); // 5 deleted since, 6 inserted again since
        await then.Received(1).ReadByKeysAsync(Orders, Arg.Is<IReadOnlyList<int>?>(k => k!.SequenceEqual(new[] { 0 })),
            Arg.Is<IReadOnlyList<IReadOnlyList<object>>>(k => k.Count == 3), true, null, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(100, "FREE/FREEPDB1", 200, "FREE/FREEPDB1", false)]
    [InlineData(100, null, 200, "FREE/FREEPDB1", false)] // saved by an older version: only the SCN counts
    [InlineData(100, "FREE/FREEPDB1", 200, "PROD/ERP", true)] // the profile reaches another database now
    [InlineData(300, "FREE/FREEPDB1", 200, "FREE/FREEPDB1", true)] // the database was set up anew under its name
    public void A_saved_point_belongs_to_the_database_by_name_and_an_scn_not_ahead_of_it(long scn, string? database, long nowScn, string nowDatabase, bool dropped) =>
        Assert.Equal(dropped, TimePoints.Mismatch(scn, database, new DatabasePoint(nowScn, nowDatabase)) is not null);

    [Fact]
    public void A_chosen_time_lies_before_now()
    {
        var now = new DateTimeOffset(2026, 11, 9, 10, 0, 0, TimeSpan.FromHours(1));

        Assert.Equal((TimeSpan.FromMinutes(15), null), TimePoints.Back(now.AddMinutes(-15), now));
        Assert.Equal(TimeSpan.Zero, TimePoints.Back(now.AddMilliseconds(500), now).Back); // a click's worth ahead counts as now
        Assert.NotNull(TimePoints.Back(now.AddMinutes(1), now).Error);
    }
}
