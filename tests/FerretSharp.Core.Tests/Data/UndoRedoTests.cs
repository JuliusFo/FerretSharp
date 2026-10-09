using FerretSharp.Core.Connections;
using FerretSharp.Core.Data;
using FerretSharp.Core.Query;
using FerretSharp.Core.Schema;

namespace FerretSharp.Core.Tests.Data;

/// <summary>WP-30: the undo and redo stacks of a transaction (<see cref="WriteLog"/>) and the edit history of a tab.</summary>
public class UndoRedoTests
{
    private static readonly TableDetails Kunden = new(
        new TableSummary("APP", "KUNDEN", TableKind.Table),
        [
            new ColumnInfo("ID", "NUMBER", null, true, 10, 0, false, false, null, 0),
            new ColumnInfo("NAME", "VARCHAR2", 20, true, null, null, true, false, null, 0),
            new ColumnInfo("ORT", "VARCHAR2", 20, true, null, null, true, false, null, 0),
        ],
        ["ID"], [], false);

    private static RowData Row(decimal id, string name, string ort = "Bonn") => new(new RowKey.PrimaryKey([id]), [id, name, ort]);

    private static WriteAction Action(string description, WriteActionKind kind = WriteActionKind.Statement, Guid? redoOf = null) =>
        new(Guid.NewGuid(), kind, description, 1, DateTimeOffset.Now) { RedoOf = redoOf, Statements = [new QuerySpec("UPDATE KUNDEN SET NAME = :p0", [])] };

    // ---------- WriteLog ----------

    [Fact]
    public void Taking_back_an_action_takes_every_later_one_along_and_they_become_redoable_oldest_first()
    {
        var log = new WriteLog();
        var (a, b, c) = (Action("a"), Action("b"), Action("c"));
        log.Add(a, "SP1");
        log.Add(b, "SP2");
        log.Add(c, "SP3");

        Assert.Equal("SP2", log.SavepointFor(b.Id, c.Id));
        Assert.Equal([c, b], log.TakeBack(b.Id)); // newest first

        Assert.Equal([a], log.Actions);
        Assert.Equal([b, c], log.Undone); // the next redo first
        Assert.Same(b, log.NextRedo);
    }

    [Fact]
    public void A_later_undo_puts_its_actions_before_the_ones_undone_earlier()
    {
        var log = new WriteLog();
        var (a, b) = (Action("a"), Action("b"));
        log.Add(a, "SP1");
        log.Add(b, "SP2");
        log.TakeBack(b.Id);
        log.TakeBack(a.Id);

        Assert.Empty(log.Actions);
        Assert.Equal([a, b], log.Undone);
    }

    [Fact]
    public void A_redo_keeps_the_later_redos_and_any_other_write_ends_them()
    {
        var log = new WriteLog();
        var (a, b) = (Action("a"), Action("b"));
        log.Add(a, "SP1");
        log.Add(b, "SP2");
        log.TakeBack(a.Id);

        var redoneA = Action("a", redoOf: log.CheckRedo(a.Id).Origin);
        log.Add(redoneA, "SP3");
        Assert.Equal([b], log.Undone);
        Assert.Equal(a.Id, redoneA.Origin);

        log.Add(Action("new"), "SP4");
        Assert.Empty(log.Undone);
    }

    [Fact]
    public void A_write_redone_twice_still_names_the_first_of_its_line()
    {
        var log = new WriteLog();
        var a = Action("a");
        log.Add(a, "SP1");
        log.TakeBack(a.Id);
        var again = Action("a", redoOf: log.CheckRedo(a.Id).Origin);
        log.Add(again, "SP2");
        log.TakeBack(again.Id);
        var third = Action("a", redoOf: log.CheckRedo(again.Id).Origin);

        Assert.Equal(a.Id, third.Origin);
    }

    [Fact]
    public void Only_the_next_redo_is_allowed()
    {
        var log = new WriteLog();
        var (a, b) = (Action("a"), Action("b"));
        log.Add(a, "SP1");
        log.Add(b, "SP2");
        log.TakeBack(a.Id);

        Assert.Throws<RefusedException>(() => log.CheckRedo(b.Id));
        Assert.Same(a, log.CheckRedo(a.Id));
    }

    [Fact]
    public void Taking_back_is_refused_for_an_action_no_longer_open_or_when_something_was_written_unseen()
    {
        var log = new WriteLog();
        var (a, b) = (Action("a"), Action("UPDATE ORDERS"));
        log.Add(a, "SP1");

        Assert.Throws<RefusedException>(() => log.SavepointFor(Guid.NewGuid(), a.Id));
        log.Add(b, "SP2");
        var refused = Assert.Throws<RefusedException>(() => log.SavepointFor(a.Id, a.Id)); // b would go along unseen
        Assert.Contains("UPDATE ORDERS", refused.Message);
    }

    [Fact]
    public void Clear_ends_actions_and_redo()
    {
        var log = new WriteLog();
        var a = Action("a");
        log.Add(a, "SP1");
        log.Add(Action("b"), "SP2");
        log.TakeBack(log.Actions[1].Id);

        log.Clear();

        Assert.Empty(log.Actions);
        Assert.Empty(log.Undone);
    }

    [Fact]
    public void A_write_action_does_not_print_its_bind_values()
    {
        var action = new WriteAction(Guid.NewGuid(), WriteActionKind.Statement, "UPDATE KUNDEN", 1, DateTimeOffset.Now)
        {
            Statements = [new QuerySpec("UPDATE KUNDEN SET NAME = :p0", [new QueryParameter("p0", "Geheim", OracleTypeHint.Varchar2)])],
        };

        Assert.DoesNotContain("Geheim", action.ToString());
        Assert.Contains("UPDATE KUNDEN", action.ToString());
    }

    // ---------- ChangeTracker: edit history ----------

    [Fact]
    public void Cell_edits_are_undone_and_redone_one_by_one()
    {
        var tracker = new ChangeTracker(Kunden);
        var row = Row(1, "Meier");
        tracker.SetValue(row, 1, "Müller");
        tracker.SetValue(row, 2, "Köln");
        tracker.SetValue(row, 1, "Schulz");

        tracker.UndoEdit();
        Assert.Equal("Müller", tracker.Find(row.Key)!.ValueOf(1));
        Assert.Equal("Köln", tracker.Find(row.Key)!.ValueOf(2));

        tracker.UndoEdit();
        tracker.UndoEdit();
        Assert.Null(tracker.Find(row.Key)); // nothing pending, the row is not tracked any more
        Assert.False(tracker.CanUndoEdit);

        tracker.RedoEdit();
        Assert.Equal("Müller", tracker.Find(row.Key)!.ValueOf(1));
        Assert.Equal("Bonn", tracker.Find(row.Key)!.ValueOf(2));
        Assert.True(tracker.CanRedoEdit);
    }

    [Fact]
    public void A_new_edit_ends_redo_and_an_edit_that_changes_nothing_is_not_remembered()
    {
        var tracker = new ChangeTracker(Kunden);
        var row = Row(1, "Meier");
        tracker.SetValue(row, 1, "Müller");
        tracker.UndoEdit();
        Assert.True(tracker.CanRedoEdit);

        tracker.SetValue(row, 1, "Meier"); // the loaded value: nothing pending, no step
        Assert.True(tracker.CanRedoEdit);
        Assert.False(tracker.CanUndoEdit);

        tracker.SetValue(row, 2, "Köln");
        Assert.False(tracker.CanRedoEdit);
    }

    [Fact]
    public void New_rows_deletions_and_discards_are_steps_of_the_history()
    {
        var tracker = new ChangeTracker(Kunden);
        var first = tracker.AddRow();
        tracker.SetValue(first, 1, "Neu 1");
        var second = tracker.AddRow();
        var loaded = Row(7, "Alt");
        tracker.Delete(loaded);
        tracker.DiscardPending();
        Assert.Equal(0, tracker.PendingCount);

        tracker.UndoEdit(); // the discard
        Assert.Equal([first, second], tracker.NewRows);
        Assert.Equal(ChangeStage.Pending, tracker.Find(loaded.Key)!.Deleted);
        Assert.Equal("Neu 1", first.ValueOf(1));

        tracker.UndoEdit(); // the deletion
        Assert.Null(tracker.Find(loaded.Key));

        tracker.Delete(second); // a new row that was never inserted disappears – and comes back at its place
        tracker.UndoEdit();
        Assert.Equal([first, second], tracker.NewRows);
    }

    [Fact]
    public void Discarding_one_cell_keeps_the_other_pending_cells_and_is_undone()
    {
        var tracker = new ChangeTracker(Kunden);
        var row = Row(1, "Meier");
        tracker.SetValue(row, 1, "Müller");
        tracker.SetValue(row, 2, "Köln");
        var change = tracker.Find(row.Key)!;

        tracker.Revert(change, 1);
        Assert.Equal([2], change.PendingColumns);

        tracker.UndoEdit();
        Assert.Equal("Müller", change.ValueOf(1));
    }

    [Fact]
    public void A_write_ends_the_edit_history()
    {
        var tracker = new ChangeTracker(Kunden);
        tracker.SetValue(Row(1, "Meier"), 1, "Müller");

        var batch = tracker.MarkFlushed(tracker.PendingOperations(), new Dictionary<Guid, RowKey>());
        Assert.False(tracker.CanUndoEdit);

        tracker.SetValue(Row(2, "Lang"), 1, "Kurz");
        tracker.UndoFlush(batch);
        Assert.False(tracker.CanUndoEdit);
    }

    [Fact]
    public void The_pending_kind_of_a_row()
    {
        var tracker = new ChangeTracker(Kunden);
        var changed = Row(1, "Meier");
        var deleted = Row(2, "Weg");
        tracker.SetValue(changed, 1, "Müller");
        tracker.Delete(deleted);
        var added = tracker.AddRow();

        Assert.Equal(OperationKind.Update, tracker.Find(changed.Key)!.PendingKind);
        Assert.Equal(OperationKind.Delete, tracker.Find(deleted.Key)!.PendingKind);
        Assert.Equal(OperationKind.Insert, added.PendingKind);

        tracker.MarkFlushed(tracker.PendingOperations(), new Dictionary<Guid, RowKey> { [added.Id] = new RowKey.RowId("AAA") });
        Assert.Null(tracker.Find(changed.Key)!.PendingKind);
    }

    // ---------- ChangeTracker: redo of a grid write ----------

    [Fact]
    public void A_write_taken_back_is_pending_as_undone_until_its_rows_are_edited()
    {
        var tracker = new ChangeTracker(Kunden);
        var row = Row(1, "Meier");
        tracker.SetValue(row, 1, "Müller");
        var added = tracker.AddRow();
        tracker.SetValue(added, 1, "Neu");
        var batch = tracker.MarkFlushed(tracker.PendingOperations(), new Dictionary<Guid, RowKey> { [added.Id] = new RowKey.RowId("AAA") });
        tracker.UndoFlush(batch);

        Assert.True(tracker.IsPendingAsUndone(batch));
        Assert.Equal(2, tracker.PendingOperations(batch).Count);

        tracker.SetValue(row, 2, "Köln"); // another cell of the same row: the write would no longer be the same
        Assert.False(tracker.IsPendingAsUndone(batch));

        tracker.UndoEdit(); // Ctrl+Z brings it back
        Assert.True(tracker.IsPendingAsUndone(batch));

        tracker.Delete(added);
        Assert.False(tracker.IsPendingAsUndone(batch));
    }

    [Fact]
    public void The_operations_of_a_write_taken_back_leave_other_pending_rows_out()
    {
        var tracker = new ChangeTracker(Kunden);
        tracker.SetValue(Row(1, "Meier"), 1, "Müller");
        var batch = tracker.MarkFlushed(tracker.PendingOperations(), new Dictionary<Guid, RowKey>());
        tracker.SetValue(Row(2, "Lang"), 1, "Kurz");
        tracker.UndoFlush(batch);

        var operations = tracker.PendingOperations(batch);

        Assert.Equal(new RowKey.PrimaryKey([1m]), Assert.Single(operations).Key);
        Assert.Equal(2, tracker.PendingOperations().Count);
    }
}
