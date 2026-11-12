using FerretSharp.Core.Data;
using FerretSharp.Core.Schema;

namespace FerretSharp.Core.Tests.Data;

/// <summary>WP-85: the key of a written row that cannot be read back (VPD) comes from the values written.</summary>
public class WrittenKeyTests
{
    private static ColumnInfo Col(string name, string type, int? length = null, bool charSemantics = true, bool nullable = true) =>
        new(name, type, length, charSemantics, type == "NUMBER" ? 10 : null, type == "NUMBER" ? 0 : null, nullable, false, null, 0);

    /// <summary>A key of two columns, one blank-padded (CHAR with BYTE semantics), and a column outside the key.</summary>
    private static readonly TableDetails Parent = new(
        new TableSummary("APP", "PARENT", TableKind.Table),
        [Col("MANDANT", "CHAR", 3, charSemantics: false, nullable: false), Col("NR", "NUMBER", nullable: false), Col("TXT", "VARCHAR2", 10)],
        ["MANDANT", "NR"], [], false);

    private static readonly TableDetails NoKey = new(
        new TableSummary("APP", "LOG", TableKind.Table), [Col("TXT", "VARCHAR2", 10)], [], [], false);

    private static RowKey Key(params object?[] values) => new RowKey.PrimaryKey(values);

    private static PendingOperation Operation(ChangeTracker tracker) => Assert.Single(tracker.PendingOperations());

    /// <summary>
    /// Cases: only NR changes – MANDANT stays as loaded; MANDANT changes – padded like a read; "Ä" takes two of the three
    /// bytes – one blank; a value of the full length – no blank.
    /// </summary>
    [Theory]
    [InlineData(1, "9", "A  ", 9)]
    [InlineData(0, "B", "B  ", 2)]
    [InlineData(0, "Ä", "Ä ", 2)]
    [InlineData(0, "XYZ", "XYZ", 2)]
    public void An_update_keeps_the_old_key_with_its_changed_columns_as_a_read_gives_them(int column, string typed, string mandant, int nr)
    {
        var tracker = new ChangeTracker(Parent);
        tracker.SetValue(new RowData(Key("A  ", 2m), ["A  ", 2m, "x"]), column, typed);

        Assert.Equal(Key(mandant, (decimal)nr), WrittenKey.Of(Parent, Operation(tracker)));
    }

    [Fact]
    public void An_insert_with_every_key_column_gets_its_key()
    {
        var tracker = new ChangeTracker(Parent);
        var row = tracker.AddRow();
        tracker.SetValue(row, 0, "C");
        tracker.SetValue(row, 1, "5");

        Assert.Equal(Key("C  ", 5m), WrittenKey.Of(Parent, Operation(tracker)));
    }

    /// <summary>A TIMESTAMP(9) key typed (7 digits at most) is a <see cref="PreciseTimestamp"/>, as a read gives it.</summary>
    [Fact]
    public void A_typed_timestamp_key_is_precise_like_a_read()
    {
        var table = new TableDetails(new TableSummary("APP", "LOG", TableKind.Table),
            [new ColumnInfo("TS", "TIMESTAMP(9)", null, false, null, 9, false, false, null, 1)], ["TS"], [], false);
        var tracker = new ChangeTracker(table);
        tracker.SetValue(tracker.AddRow(), 0, "01.10.2026 14:30:05,1234567");

        Assert.Equal(Key(new PreciseTimestamp(new DateTime(2026, 10, 1, 14, 30, 5), 123456700)), WrittenKey.Of(table, Operation(tracker)));
    }

    /// <summary>A key column left to the database (identity, trigger, default), or a table keyed by ROWID: the ROWID stays.</summary>
    [Fact]
    public void Without_every_key_column_or_without_a_primary_key_there_is_none()
    {
        var parent = new ChangeTracker(Parent);
        parent.SetValue(parent.AddRow(), 0, "C");
        var log = new ChangeTracker(NoKey);
        log.SetValue(log.AddRow(), 0, "x");

        Assert.Null(WrittenKey.Of(Parent, Operation(parent)));
        Assert.Null(WrittenKey.Of(NoKey, Operation(log)));
    }
}
