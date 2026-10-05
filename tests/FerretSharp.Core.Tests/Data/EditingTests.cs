using FerretSharp.Core.Data;
using FerretSharp.Core.Oracle;
using FerretSharp.Core.Query;
using FerretSharp.Core.Schema;

namespace FerretSharp.Core.Tests.Data;

public class EditingTests
{
    private static ColumnInfo Col(string name, string type, int? length = null, bool charSemantics = true, int? precision = null, int? scale = null,
        bool nullable = true, bool identity = false, bool isVirtual = false) =>
        new(name, type, length, charSemantics, precision, scale, nullable, identity, null, 0, IsVirtual: isVirtual);

    private static readonly TableDetails Kunden = new(
        new TableSummary("APP", "KUNDEN", TableKind.Table),
        [
            Col("ID", "NUMBER", precision: 10, scale: 0, nullable: false),
            Col("NAME", "VARCHAR2", 10, nullable: false),
            Col("KZ", "CHAR", 3, charSemantics: false),
            Col("BETRAG", "NUMBER", precision: 6, scale: 2),
            Col("DATUM", "DATE"),
            Col("TS", "TIMESTAMP(6)", scale: 6),
            Col("DATEN", "RAW", 4),
            Col("NOTIZ", "CLOB"),
            Col("LFD", "NUMBER", identity: true),
            Col("GROSS", "VARCHAR2", 10, isVirtual: true),
        ],
        ["ID"], [], false);

    private static ColumnInfo C(string name) => Kunden.Columns.Single(c => c.Name == name);

    private static int I(string name) => Kunden.Columns.ToList().FindIndex(c => c.Name == name);

    private static RowData Row(decimal id, string name, decimal? betrag = null) =>
        new(new RowKey.PrimaryKey([id]), [id, name, null, betrag, null, null, null, null, 1m, name.ToUpperInvariant()]);

    // ---------- OracleTypeMapper ----------

    [Theory]
    [InlineData("BETRAG", "1234,5", 1234.5)]
    [InlineData("BETRAG", "1.234,50", 1234.5)]
    [InlineData("BETRAG", "1234.5", 1234.5)]
    [InlineData("ID", "4711", 4711)]
    public void Numbers_are_parsed_german_or_invariant(string column, string text, double expected) =>
        Assert.Equal((decimal)expected, OracleTypeMapper.Parse(C(column), text).Value);

    [Theory]
    [InlineData("BETRAG", "1,234", "Höchstens 2 Nachkommastellen")]
    [InlineData("BETRAG", "12345", "höchstens 4 Stellen vor dem Komma")]
    [InlineData("ID", "1,5", "nur ganze Zahlen")]
    [InlineData("BETRAG", "zwölf", "keine Zahl")]
    [InlineData("NAME", "", "darf nicht leer sein")]
    [InlineData("NAME", "elf Zeichen", "Zu lang: 11 Zeichen, erlaubt sind 10")]
    [InlineData("KZ", "ÄÖ", "Zu lang: 4 Bytes, erlaubt sind 3")]
    [InlineData("DATUM", "31.02.2026", "kein Datum")]
    [InlineData("DATEN", "XYZ", "kein Hex-Wert")]
    [InlineData("DATEN", "0102030405", "Zu lang: 5 Bytes, erlaubt sind 4")]
    public void Invalid_input_is_explained(string column, string text, string message)
    {
        var parsed = OracleTypeMapper.Parse(C(column), text);

        Assert.False(parsed.IsValid);
        Assert.Contains(message, parsed.Error);
    }

    [Fact]
    public void Empty_is_null_and_dates_timestamps_and_raw_parse()
    {
        Assert.Equal(ParsedValue.Ok(null), OracleTypeMapper.Parse(C("BETRAG"), "  "));
        Assert.Equal(new DateTime(2026, 10, 5, 14, 30, 0), OracleTypeMapper.Parse(C("DATUM"), "05.10.2026 14:30").Value);
        Assert.Equal(new DateTime(2026, 10, 5, 14, 30, 1).AddTicks(1234560), OracleTypeMapper.Parse(C("TS"), "05.10.2026 14:30:01.123456").Value);
        Assert.Equal([0xCA, 0xFE], (byte[])OracleTypeMapper.Parse(C("DATEN"), "0xCAFE").Value!);
        Assert.Equal("  ", OracleTypeMapper.Parse(C("NAME"), "  ").Value); // text keeps its blanks
    }

    [Fact]
    public void Edit_text_is_the_full_value_and_parses_back()
    {
        var long_ = new string('x', 2000) + "\nzweite Zeile";
        var text = Col("T", "VARCHAR2", 4000);
        Assert.Equal(long_, OracleTypeMapper.EditText(text, long_));

        foreach (var (column, value) in new (string, object)[] { ("BETRAG", 1234.5m), ("DATUM", new DateTime(2026, 1, 2, 3, 4, 5)), ("DATEN", new byte[] { 1, 0xAB }) })
        {
            var edit = OracleTypeMapper.EditText(C(column), value);
            Assert.True(OracleTypeMapper.ValuesEqual(value, OracleTypeMapper.Parse(C(column), edit).Value), $"{column}: {edit}");
        }

        Assert.Equal("1234,5", OracleTypeMapper.EditText(C("BETRAG"), 1234.5m));
    }

    [Theory]
    [InlineData("NAME", false, null)]
    [InlineData("ID", false, "nur bei neuen Zeilen")]
    [InlineData("ID", true, null)]
    [InlineData("NOTIZ", false, "lässt sich hier nicht bearbeiten")]
    [InlineData("LFD", true, "Identity")]
    [InlineData("GROSS", false, "Virtuelle Spalte")]
    public void Editable_columns(string column, bool newRow, string? reason)
    {
        var actual = OracleTypeMapper.NotEditableReason(Kunden, C(column), newRow);
        if (reason is null)
        {
            Assert.Null(actual);
        }
        else
        {
            Assert.Contains(reason, actual);
        }
    }

    [Fact]
    public void Views_and_tables_without_key_are_read_only()
    {
        var view = Kunden with { Table = new TableSummary("APP", "V", TableKind.View), PrimaryKey = [] };
        Assert.Contains("nur lesbar", OracleTypeMapper.NotEditableReason(view, C("NAME"), false));
    }

    [Fact]
    public void Bind_types_follow_the_column()
    {
        Assert.Equal(OracleTypeHint.Char, OracleTypeMapper.BindType(C("KZ")));
        Assert.Equal(OracleTypeHint.TimeStamp, OracleTypeMapper.BindType(C("TS")));
        Assert.Equal(OracleTypeHint.NVarchar2, OracleTypeMapper.BindType(Col("N", "NVARCHAR2", 5)));
    }

    // ---------- ChangeTracker ----------

    [Fact]
    public void Editing_a_cell_makes_a_pending_update_and_editing_back_removes_it()
    {
        var tracker = new ChangeTracker(Kunden);
        var row = Row(1, "Müller", 10m);

        var result = tracker.SetValue(row, I("BETRAG"), "12,5");

        Assert.True(result.IsValid);
        var change = tracker.Find(row.Key)!;
        Assert.Equal(12.5m, change.ValueOf(I("BETRAG")));
        Assert.Equal(ChangeStage.Pending, change.StageOf(I("BETRAG")));
        Assert.Null(change.StageOf(I("NAME")));
        Assert.Equal(1, tracker.PendingCount);

        var update = Assert.Single(tracker.PendingOperations());
        Assert.Equal(OperationKind.Update, update.Kind);
        Assert.Equal(12.5m, update.Values[I("BETRAG")]);
        Assert.Equal(10m, update.Expected[I("BETRAG")]);

        tracker.SetValue(row, I("BETRAG"), "10,00");
        Assert.Null(tracker.Find(row.Key));
        Assert.Empty(tracker.PendingOperations());
    }

    [Fact]
    public void Invalid_or_not_editable_input_changes_nothing()
    {
        var tracker = new ChangeTracker(Kunden);
        var row = Row(1, "Müller");

        Assert.False(tracker.SetValue(row, I("ID"), "2").IsValid);
        Assert.False(tracker.SetValue(row, I("BETRAG"), "abc").IsValid);
        Assert.False(tracker.HasChanges);
    }

    [Fact]
    public void After_flush_the_flushed_value_is_what_the_next_update_expects()
    {
        var tracker = new ChangeTracker(Kunden);
        var row = Row(1, "Müller", 10m);
        tracker.SetValue(row, I("BETRAG"), "20");
        tracker.MarkFlushed(tracker.PendingOperations(), new Dictionary<Guid, RowKey>());

        var change = tracker.Find(row.Key)!;
        Assert.Equal(ChangeStage.Flushed, change.StageOf(I("BETRAG")));
        Assert.Equal(0, tracker.PendingCount);
        Assert.Equal(1, tracker.FlushedCount);

        tracker.SetValue(row, I("BETRAG"), "30");
        Assert.Equal(20m, Assert.Single(tracker.PendingOperations()).Expected[I("BETRAG")]);

        tracker.SetValue(row, I("BETRAG"), "20"); // back to the flushed value: nothing pending
        Assert.Empty(tracker.PendingOperations());
        Assert.Equal(ChangeStage.Flushed, change.StageOf(I("BETRAG")));
    }

    [Fact]
    public void Delete_replaces_pending_edits_and_revert_undoes_it()
    {
        var tracker = new ChangeTracker(Kunden);
        var row = Row(1, "Müller");
        tracker.SetValue(row, I("NAME"), "Meier");

        tracker.Delete(row);

        var delete = Assert.Single(tracker.PendingOperations());
        Assert.Equal(OperationKind.Delete, delete.Kind);
        Assert.False(tracker.SetValue(row, I("NAME"), "X").IsValid);

        tracker.Revert(row.Key);
        Assert.False(tracker.HasChanges);
    }

    [Fact]
    public void New_rows_insert_only_filled_columns_and_vanish_when_deleted_before_flush()
    {
        var tracker = new ChangeTracker(Kunden);
        var row = tracker.AddRow();
        Assert.True(tracker.SetValue(row, I("ID"), "7").IsValid); // key editable in a new row
        Assert.True(tracker.SetValue(row, I("NAME"), "").IsValid); // empty allowed while filling in
        tracker.SetValue(row, I("BETRAG"), "1,5");

        var insert = Assert.Single(tracker.PendingOperations());
        Assert.Equal(OperationKind.Insert, insert.Kind);
        Assert.Equal([I("ID"), I("BETRAG")], insert.Values.Keys.Order());

        tracker.Delete(row);
        Assert.Empty(tracker.PendingOperations());
        Assert.False(tracker.HasChanges);
    }

    [Fact]
    public void Inserted_row_is_tracked_by_its_new_key_and_updated_without_concurrency_check()
    {
        var tracker = new ChangeTracker(Kunden);
        var row = tracker.AddRow();
        tracker.SetValue(row, I("ID"), "7");
        tracker.SetValue(row, I("NAME"), "Neu");
        var key = new RowKey.PrimaryKey([7m]);

        tracker.MarkFlushed(tracker.PendingOperations(), new Dictionary<Guid, RowKey> { [row.Id] = key });

        Assert.Empty(tracker.NewRows);
        Assert.Same(row, tracker.Find(key));
        Assert.Equal(ChangeStage.Flushed, row.StageOf(I("NAME")));

        var loaded = new RowData(key, [7m, "Neu", null, null, null, null, null, null, 2m, "NEU"]);
        tracker.SetValue(loaded, I("NAME"), "Neuer");
        var update = Assert.Single(tracker.PendingOperations());
        Assert.Equal(OperationKind.Update, update.Kind);
        Assert.Empty(update.Expected);
    }

    [Fact]
    public void Operations_come_as_deletes_then_updates_then_inserts()
    {
        var tracker = new ChangeTracker(Kunden);
        tracker.AddRow();
        tracker.SetValue(Row(1, "A"), I("NAME"), "B");
        tracker.Delete(Row(2, "C"));

        Assert.Equal([OperationKind.Delete, OperationKind.Update, OperationKind.Insert], tracker.PendingOperations().Select(o => o.Kind));
    }

    [Fact]
    public void Undoing_a_flush_makes_its_changes_pending_again()
    {
        var tracker = new ChangeTracker(Kunden);
        var row = Row(1, "Müller", 10m);
        var deleted = Row(2, "Weg");
        tracker.SetValue(row, I("BETRAG"), "20");
        tracker.Delete(deleted);
        var added = tracker.AddRow();
        tracker.SetValue(added, I("ID"), "9");
        var batch = tracker.MarkFlushed(tracker.PendingOperations(), new Dictionary<Guid, RowKey> { [added.Id] = new RowKey.RowId("AAA") });
        tracker.SetValue(row, I("NAME"), "Meier"); // edited after the flush: stays pending

        tracker.UndoFlush(batch);

        Assert.Equal(0, tracker.FlushedCount);
        Assert.Equal(
            [OperationKind.Delete, OperationKind.Update, OperationKind.Insert],
            tracker.PendingOperations().Select(o => o.Kind));
        var update = tracker.PendingOperations().Single(o => o.Kind == OperationKind.Update);
        Assert.Equal(20m, update.Values[I("BETRAG")]);
        Assert.Equal("Meier", update.Values[I("NAME")]);
        Assert.Equal(10m, update.Expected[I("BETRAG")]);
        Assert.Same(added, Assert.Single(tracker.NewRows));
        Assert.Equal(RowKey.None.Instance, added.Key);
    }

    [Fact]
    public void Discard_drops_pending_but_keeps_flushed()
    {
        var tracker = new ChangeTracker(Kunden);
        var row = Row(1, "Müller", 10m);
        tracker.SetValue(row, I("BETRAG"), "20");
        tracker.MarkFlushed(tracker.PendingOperations(), new Dictionary<Guid, RowKey>());
        tracker.SetValue(row, I("NAME"), "Meier");
        tracker.AddRow();

        tracker.DiscardPending();

        Assert.Empty(tracker.PendingOperations());
        Assert.Equal(1, tracker.FlushedCount);
        Assert.Equal(20m, tracker.Find(row.Key)!.ValueOf(I("BETRAG")));
    }
}
