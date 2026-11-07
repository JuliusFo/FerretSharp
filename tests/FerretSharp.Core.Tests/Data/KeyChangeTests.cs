using System.Text.RegularExpressions;
using FerretSharp.Core.Data;
using FerretSharp.Core.Oracle;
using FerretSharp.Core.Query;
using FerretSharp.Core.Schema;

namespace FerretSharp.Core.Tests.Data;

/// <summary>Changing the primary key of loaded rows (ADR 0007 addendum): the write, the tracker, undo and redo, the references.</summary>
public class KeyChangeTests
{
    private static ColumnInfo Col(string name, string type, int? precision = null, bool nullable = true) =>
        new(name, type, type == "VARCHAR2" ? 20 : null, true, precision, precision is null ? null : 0, nullable, false, null, 0);

    private static readonly TableDetails Kunden = new(
        new TableSummary("APP", "KUNDEN", TableKind.Table),
        [Col("ID", "NUMBER", 10, nullable: false), Col("NAME", "VARCHAR2"), Col("ORT", "VARCHAR2")],
        ["ID"], [], false);

    private static readonly TableRef KundenRef = new("APP", "KUNDEN");

    private static readonly ForeignKeyInfo AuftragKunde =
        new("FK_AUFTRAG_KUNDE", new TableRef("APP", "AUFTRAG"), ["KUNDE_ID"], KundenRef, ["ID"], FkSource.Declared);

    private static readonly ForeignKeyInfo NotizKunde =
        new("NOTIZ_KUNDE", new TableRef("APP", "NOTIZ"), ["KUNDE"], KundenRef, ["ID"], FkSource.ClrModel);

    /// <summary>A relationship into another column than the key: a change of the key alone does not concern it.</summary>
    private static readonly ForeignKeyInfo ByName =
        new("BRIEF_NAME", new TableRef("APP", "BRIEF"), ["EMPFAENGER"], KundenRef, ["NAME"], FkSource.Manual);

    private static RowData Row(decimal id, string name) => new(new RowKey.PrimaryKey([id]), [id, name, "Bonn"]);

    private static RowKey Key(decimal id) => new RowKey.PrimaryKey([id]);

    // ---------- tracker ----------

    [Fact]
    public void The_key_of_a_loaded_row_can_be_edited_and_is_written_by_the_old_key()
    {
        var tracker = new ChangeTracker(Kunden);
        var row = Row(1, "Müller");

        var result = tracker.SetValue(row, 0, "7");

        Assert.True(result.IsValid);
        var update = Assert.Single(tracker.PendingOperations());
        Assert.Equal(Key(1), update.Key);
        Assert.Equal(7m, update.Values[0]);
        Assert.Equal(1m, update.Expected[0]);
    }

    [Fact]
    public void After_the_write_the_row_goes_on_with_its_new_key()
    {
        var tracker = new ChangeTracker(Kunden);
        var row = Row(1, "Müller");
        tracker.SetValue(row, 0, "7");
        var change = tracker.Find(row.Key)!;

        tracker.MarkFlushed(tracker.PendingOperations(), new Dictionary<Guid, RowKey> { [change.Id] = Key(7) });

        Assert.Null(tracker.Find(Key(1)));
        Assert.Same(change, tracker.Find(Key(7)));
        Assert.Equal(1, tracker.FlushedCount);
        Assert.Equal(7m, change.ValueOf(0));

        // The reloaded row (found by its new key) is edited further: written by the new key, compared with the new value.
        tracker.SetValue(new RowData(Key(7), [7m, "Müller", "Bonn"]), 1, "Meier");
        var next = Assert.Single(tracker.PendingOperations());
        Assert.Equal(Key(7), next.Key);
        Assert.Same(change, next.Change);
    }

    [Fact]
    public void Undoing_the_write_gives_the_row_its_old_key_back_and_a_redo_writes_the_change_again()
    {
        var tracker = new ChangeTracker(Kunden);
        var row = Row(1, "Müller");
        tracker.SetValue(row, 0, "7");
        var change = tracker.Find(row.Key)!;
        var batch = tracker.MarkFlushed(tracker.PendingOperations(), new Dictionary<Guid, RowKey> { [change.Id] = Key(7) });

        tracker.UndoFlush(batch);

        Assert.Null(tracker.Find(Key(7)));
        Assert.Same(change, tracker.Find(Key(1)));
        Assert.Equal(0, tracker.FlushedCount);
        Assert.True(tracker.IsPendingAsUndone(batch));
        var redo = Assert.Single(tracker.PendingOperations(batch));
        Assert.Equal(Key(1), redo.Key);
        Assert.Equal(7m, redo.Values[0]);

        tracker.MarkFlushed([redo], new Dictionary<Guid, RowKey> { [change.Id] = Key(7) });
        Assert.Same(change, tracker.Find(Key(7)));
        Assert.Null(tracker.Find(Key(1)));
    }

    [Fact]
    public void Two_writes_of_the_key_are_taken_back_newest_first()
    {
        var tracker = new ChangeTracker(Kunden);
        tracker.SetValue(Row(1, "Müller"), 0, "7");
        var change = tracker.Find(Key(1))!;
        var first = tracker.MarkFlushed(tracker.PendingOperations(), new Dictionary<Guid, RowKey> { [change.Id] = Key(7) });
        tracker.SetValue(new RowData(Key(7), [7m, "Müller", "Bonn"]), 0, "9");
        var second = tracker.MarkFlushed(tracker.PendingOperations(), new Dictionary<Guid, RowKey> { [change.Id] = Key(9) });

        tracker.UndoFlush(second);
        Assert.Same(change, tracker.Find(Key(7)));
        Assert.Null(tracker.Find(Key(9)));

        tracker.UndoFlush(first);
        Assert.Same(change, tracker.Find(Key(1)));
        Assert.Null(tracker.Find(Key(7)));
        Assert.Equal(9m, Assert.Single(tracker.PendingOperations()).Values[0]); // the newest value stays pending
    }

    /// <summary>
    /// Review of `small-pk-edit`, 5: a row deleted and its key taken by another row in the same write (or inserted again) pushed
    /// the written delete out of the tracker – one written change too few, missing in the change overview.
    /// </summary>
    [Fact]
    public void A_written_delete_whose_key_another_row_takes_stays_counted_and_comes_back_on_undo()
    {
        var tracker = new ChangeTracker(Kunden);
        var gone = Row(3, "Weg");
        tracker.Delete(gone);
        tracker.SetValue(Row(1, "Müller"), 0, "3");
        var moved = tracker.Find(Key(1))!;
        var deleted = tracker.Find(Key(3))!;

        var batch = tracker.MarkFlushed(tracker.PendingOperations(), new Dictionary<Guid, RowKey> { [moved.Id] = Key(3) });

        Assert.Equal(2, tracker.FlushedCount);
        Assert.Contains(deleted, tracker.Changes);
        Assert.Same(moved, tracker.Find(Key(3))); // the row the grid shows under that key

        tracker.UndoFlush(batch);
        Assert.Same(deleted, tracker.Find(Key(3)));
        Assert.Same(moved, tracker.Find(Key(1)));
        Assert.Equal(2, tracker.PendingCount);

        // The same for a new row with the deleted row's key.
        tracker.DiscardPending();
        tracker.Delete(gone);
        tracker.MarkFlushed(tracker.PendingOperations(), new Dictionary<Guid, RowKey>());
        var added = tracker.AddRow();
        tracker.SetValue(added, 0, "3");
        tracker.MarkFlushed(tracker.PendingOperations(), new Dictionary<Guid, RowKey> { [added.Id] = Key(3) });
        Assert.Equal(2, tracker.FlushedCount);
    }

    [Fact]
    public void A_value_for_many_rows_may_go_into_the_key_too()
    {
        var tracker = new ChangeTracker(Kunden);

        var result = tracker.SetCells([Row(1, "A"), Row(2, "B")], [0], CellFill.Value("5"));

        Assert.Null(result.Error);
        Assert.Empty(result.Columns);
        Assert.Equal(2, result.Cells); // Oracle refuses the second one as a duplicate key when it is written
        Assert.Null(ChangeTracker.FillUnavailable(Kunden, Kunden.Columns[0]));
    }

    // ---------- DML ----------

    [Fact]
    public void An_update_of_the_key_returns_the_rowid_to_read_the_new_key_by()
    {
        var spec = DmlBuilder.Update(Kunden, Key(1), new Dictionary<int, object?> { [0] = 7m, [1] = "Meier" });

        Assert.Equal(
            "UPDATE \"APP\".\"KUNDEN\"\n   SET \"ID\" = :v0,\n       \"NAME\" = :v1\n WHERE \"ID\" = :k0\nRETURNING ROWID INTO :p_rowid",
            spec.Sql);
        var output = spec.Parameters.Single(p => p.Output);
        Assert.Equal((DmlBuilder.RowIdOutput, OracleTypeHint.RowId), (output.Name, output.Type));
        Assert.Equal(spec.Parameters.Select(p => p.Name).Order(), Regex.Matches(spec.Sql, @":(\w+)").Select(m => m.Groups[1].Value).Order());
        Assert.True(OracleSession.IsWriteStatement(spec.Sql));
    }

    [Fact]
    public void Other_updates_return_nothing()
    {
        Assert.DoesNotContain("RETURNING", DmlBuilder.Update(Kunden, Key(1), new Dictionary<int, object?> { [1] = "Meier" }).Sql);

        // A key that is not enforced does not find the rows (ROWID does): changing it changes no row's identity.
        var notEnforced = Kunden with { PrimaryKeyEnforced = false };
        Assert.False(DmlBuilder.ChangesKey(notEnforced, [0]));
        Assert.DoesNotContain("RETURNING", DmlBuilder.Update(notEnforced, new RowKey.RowId("AAA"), new Dictionary<int, object?> { [0] = 7m }).Sql);
    }

    // ---------- references ----------

    [Fact]
    public void Only_updates_of_the_key_are_key_changes()
    {
        var tracker = new ChangeTracker(Kunden);
        tracker.SetValue(Row(1, "A"), 0, "7");
        tracker.SetValue(Row(2, "B"), 1, "C");
        tracker.Delete(Row(3, "D"));

        var change = Assert.Single(KeyChanges.Of(Kunden, tracker.PendingOperations()));
        Assert.Equal(Key(1), change.Key);
    }

    [Fact]
    public void References_look_for_the_old_keys_along_every_relationship_into_the_changed_columns()
    {
        var tracker = new ChangeTracker(Kunden);
        tracker.SetValue(Row(1, "A"), 0, "7");
        tracker.SetValue(Row(2, "B"), 0, "8");
        var changes = KeyChanges.Of(Kunden, tracker.PendingOperations());

        var jumps = KeyChanges.References(Kunden, changes, [AuftragKunde, ByName, NotizKunde]);

        Assert.Equal(["FK_AUFTRAG_KUNDE", "NOTIZ_KUNDE"], jumps.Select(j => j.ForeignKey.Name));
        var filter = Assert.Single(jumps[0].Filters);
        Assert.Equal(("KUNDE_ID", FilterOperator.In), (filter.Column, filter.Op));
        Assert.Equal(["1", "2"], filter.Values); // the keys the rows still have in the database
        Assert.Equal(new TableRef("APP", "NOTIZ"), jumps[1].Table);
    }

    [Fact]
    public void A_key_written_before_is_looked_for_as_written()
    {
        var tracker = new ChangeTracker(Kunden);
        tracker.SetValue(Row(1, "A"), 0, "7");
        var change = tracker.Find(Key(1))!;
        tracker.MarkFlushed(tracker.PendingOperations(), new Dictionary<Guid, RowKey> { [change.Id] = Key(7) });
        tracker.SetValue(new RowData(Key(7), [7m, "A", "Bonn"]), 0, "9");

        var jump = Assert.Single(KeyChanges.References(Kunden, KeyChanges.Of(Kunden, tracker.PendingOperations()), [AuftragKunde]));

        Assert.Equal("KUNDE_ID = 7", jump.Condition);
    }

    /// <summary>
    /// Review of `small-pk-edit`, 2: a key that is not enforced finds no row (ROWID does), but relationships without a constraint
    /// point to it all the same – its change was written without a look at them.
    /// </summary>
    [Fact]
    public void A_key_that_is_not_enforced_is_a_key_change_too()
    {
        var notEnforced = Kunden with { PrimaryKeyEnforced = false };
        var tracker = new ChangeTracker(notEnforced);
        tracker.SetValue(new RowData(new RowKey.RowId("AAA"), [1m, "A", "Bonn"]), 0, "7");

        var jump = Assert.Single(KeyChanges.References(notEnforced, KeyChanges.Of(notEnforced, tracker.PendingOperations()), [NotizKunde]));

        Assert.Equal("KUNDE = 1", jump.Condition);
    }

    /// <summary>
    /// Review of `small-pk-edit`, 6: a row inserted in this transaction is looked for by the key Oracle stored (a trigger filled
    /// it), not by what was typed (nothing).
    /// </summary>
    [Fact]
    public void A_row_inserted_in_the_transaction_is_looked_for_by_its_stored_key()
    {
        var tracker = new ChangeTracker(Kunden);
        var added = tracker.AddRow();
        tracker.SetValue(added, 1, "Neu"); // the key is left to the trigger
        tracker.MarkFlushed(tracker.PendingOperations(), new Dictionary<Guid, RowKey> { [added.Id] = Key(9) });
        tracker.SetValue(new RowData(Key(9), [9m, "Neu", null]), 0, "10");

        var jump = Assert.Single(KeyChanges.References(Kunden, KeyChanges.Of(Kunden, tracker.PendingOperations()), [AuftragKunde]));

        Assert.True(jump.IsAvailable, jump.Unavailable);
        Assert.Equal("KUNDE_ID = 9", jump.Condition);
    }
}
