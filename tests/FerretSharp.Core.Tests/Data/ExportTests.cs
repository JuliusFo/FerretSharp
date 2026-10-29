using FerretSharp.Core.Data;
using FerretSharp.Core.Query;
using FerretSharp.Core.Schema;

namespace FerretSharp.Core.Tests.Data;

public class ExportTests
{
    private static ColumnInfo Col(string name, string type, bool identity = false) =>
        new(name, type, null, false, null, null, true, identity, null, 0);

    private static readonly TableDetails Table = new(
        new TableSummary("APP", "KUNDEN", TableKind.Table),
        [
            Col("ID", "NUMBER", identity: true), Col("NAME", "VARCHAR2"), Col("BETRAG", "NUMBER"), Col("DATUM", "DATE"),
            Col("TS", "TIMESTAMP(6)"), Col("GUID", "RAW"), Col("NOTIZ", "CLOB"), Col("BILD", "BLOB"), Col("TITEL", "NVARCHAR2"),
        ],
        ["ID"], [], false);

    private static RowData Row(params object?[] values) => new(RowKey.None.Instance, values);

    private static readonly RowData Full = Row(
        1234m, "O'Brien; \"Ltd\"", 1234.5m, new DateTime(2026, 10, 1, 13, 45, 7), new DateTime(2026, 10, 1, 13, 45, 7).AddTicks(1234560),
        new byte[] { 0xCA, 0xFE }, new LobValue("kurz", 4), new LobValue(null, 2048), "Grüße");

    private static readonly RowData Nulls = Row(2m, null, null, null, null, null, null, null, null);

    [Fact]
    public void Csv_uses_german_notation_quotes_where_needed_and_empty_null()
    {
        var export = DelimitedExport.Build(Table, [Full, Nulls], DelimitedExport.CsvSeparator);

        var lines = export.Text.Split("\r\n");
        Assert.Equal("ID;NAME;BETRAG;DATUM;TS;GUID;NOTIZ;BILD;TITEL", lines[0]);
        Assert.Equal("1234;\"O'Brien; \"\"Ltd\"\"\";1234,5;01.10.2026 13:45:07;01.10.2026 13:45:07.123456;CAFE;kurz;;Grüße", lines[1]);
        Assert.Equal("2;;;;;;;;", lines[2]);
        Assert.Equal(2, export.Rows);
    }

    [Fact]
    public void Clipboard_text_is_tab_separated()
    {
        var export = DelimitedExport.Build(Table, [Nulls], DelimitedExport.ClipboardSeparator);

        Assert.StartsWith("ID\tNAME\tBETRAG", export.Text);
    }

    [Fact]
    public void Incomplete_lobs_are_left_out_with_a_warning()
    {
        var longClob = Row(3m, null, null, null, null, null, new LobValue(new string('x', 200), 5000), new LobValue(null, 10), null);

        var csv = DelimitedExport.Build(Table, [Full, longClob], DelimitedExport.CsvSeparator);
        var insert = InsertExport.Build(Table, [Full, longClob]);

        Assert.Equal(
            ["BILD (BLOB): 2 Werte nicht exportiert – nur die Länge geladen.", "NOTIZ (CLOB): 1 Wert nicht exportiert – nur die Vorschau geladen."],
            csv.Warnings);
        Assert.Equal(csv.Warnings, insert.Warnings);
        Assert.Contains("NULL /* CLOB (5.000 Zeichen) nicht exportiert */", insert.Text);
    }

    [Fact]
    public void Insert_script_has_exact_literals()
    {
        var export = InsertExport.Build(Table, [Full]);

        var insert = export.Text.Split("\r\n").Single(l => l.StartsWith("INSERT", StringComparison.Ordinal));
        Assert.Equal(
            "INSERT INTO \"APP\".\"KUNDEN\" (\"ID\", \"NAME\", \"BETRAG\", \"DATUM\", \"TS\", \"GUID\", \"NOTIZ\", \"BILD\", \"TITEL\") VALUES (" +
            "1234, 'O''Brien; \"Ltd\"', 1234.5, TO_DATE('2026-10-01 13:45:07', 'YYYY-MM-DD HH24:MI:SS'), " +
            "TO_TIMESTAMP('2026-10-01 13:45:07.1234560', 'YYYY-MM-DD HH24:MI:SS.FF7'), HEXTORAW('CAFE'), 'kurz', " +
            "NULL /* BLOB (2.048 Bytes) nicht exportiert */, N'Grüße');",
            insert);
    }

    [Fact]
    public void Insert_script_header_names_the_source_and_warns_about_identity_and_ampersand()
    {
        var export = InsertExport.Build(Table, [Row(1m, "A & B", null, null, null, null, null, null, null)]);

        var lines = export.Text.Split("\r\n");
        Assert.Equal("-- 1 Zeile aus \"APP\".\"KUNDEN\" (FerretSharp-Export)", lines[0]);
        Assert.Contains(lines, l => l.Contains("SET DEFINE OFF", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("-- ID ist eine Identity-Spalte", StringComparison.Ordinal));
    }

    /// <summary>Review of R8: a quoted name with a line break left the rest of the header as an executable line.</summary>
    [Fact]
    public void A_name_with_a_line_break_stays_a_comment_in_the_header()
    {
        var table = new TableDetails(new TableSummary("APP", "K\nDROP TABLE X", TableKind.Table), [Col("I\r\nD", "NUMBER", identity: true)], [], [], false);

        var export = InsertExport.Build(table, [Row(1m)]);

        var header = export.Text.Split("\r\n").TakeWhile(l => !l.StartsWith("INSERT", StringComparison.Ordinal)).ToList();
        Assert.Equal(4, header.Count);
        Assert.All(header, line => Assert.StartsWith("-- ", line));
    }

    [Fact]
    public void Cell_text_is_the_full_value_not_the_display_text()
    {
        var text = Col("T", "VARCHAR2");
        var longText = new string('x', 1500) + "\r\nZeile 2";
        var raw = Col("R", "RAW");
        var bytes = Enumerable.Range(0, 40).Select(i => (byte)i).ToArray();

        Assert.EndsWith(" …", CellFormatter.Format(text, longText));
        Assert.Equal(longText, DelimitedExport.CellText(text, longText).Text);
        Assert.Equal("1234,5", DelimitedExport.CellText(Col("N", "NUMBER"), 1234.5m).Text);
        Assert.Equal("1.234,5", CellFormatter.Format(Col("N", "NUMBER"), 1234.5m));
        Assert.Equal(Convert.ToHexString(bytes), DelimitedExport.CellText(raw, bytes).Text);
        Assert.Equal("", DelimitedExport.CellText(text, null).Text);
        Assert.Empty(DelimitedExport.CellText(text, "a").Warnings);
    }

    [Fact]
    public void Cell_text_of_a_previewed_lob_is_left_out_with_a_warning()
    {
        var copy = DelimitedExport.CellText(Col("NOTIZ", "CLOB"), new LobValue(new string('x', 200), 5000));

        Assert.Equal("", copy.Text);
        Assert.Equal(["NOTIZ (CLOB): 1 Wert nicht exportiert – nur die Vorschau geladen."], copy.Warnings);
    }

    [Fact]
    public void Special_values_become_oracle_literals()
    {
        var warnings = new ExportWarnings();

        Assert.Equal("BINARY_DOUBLE_NAN", InsertExport.Literal(Col("D", "BINARY_DOUBLE"), double.NaN, warnings));
        Assert.Equal("-BINARY_FLOAT_INFINITY", InsertExport.Literal(Col("F", "BINARY_FLOAT"), float.NegativeInfinity, warnings));
        Assert.Equal("1.5d", InsertExport.Literal(Col("D", "BINARY_DOUBLE"), 1.5d, warnings));
        Assert.Equal("12345678901234567890123456789012345678", InsertExport.Literal(Col("N", "NUMBER"), new BigNumber("12345678901234567890123456789012345678"), warnings));
        Assert.Equal("TRUE", InsertExport.Literal(Col("B", "BOOLEAN"), true, warnings));
        Assert.Equal("EMPTY_CLOB()", InsertExport.Literal(Col("C", "CLOB"), new LobValue("", 0), warnings));
        Assert.Equal("TO_DSINTERVAL('+01 02:03:04.000000')", InsertExport.Literal(Col("I", "INTERVAL DAY(2) TO SECOND(6)"), "+01 02:03:04.000000", warnings));
        Assert.Empty(warnings.ToList());
    }

    /// <summary>
    /// Review 3.44.0, A12: TIMESTAMP(8) and (9) are read as DateTime (100 ns); only the IN list said so, the INSERT script
    /// ("reproduces them exactly"), CSV and Excel cut silently. Once per column; a TIMESTAMP(6) says nothing.
    /// </summary>
    [Fact]
    public void Exports_of_a_finer_timestamp_say_its_fractions_are_cut()
    {
        var fine = new ColumnInfo("ZEIT", "TIMESTAMP(9)", null, false, null, 9, true, false, null, 0);
        var plain = new ColumnInfo("ZEIT6", "TIMESTAMP(6)", null, false, null, 6, true, false, null, 0);
        var value = new DateTime(2026, 10, 1, 13, 45, 7).AddTicks(1234567);
        var expected = "ZEIT (TIMESTAMP(9)): Die Werte werden auf 7 Nachkommastellen gelesen – feinere Bruchteile fehlen im Export.";

        var insert = new ExportWarnings();
        InsertExport.Literal(fine, value, insert);
        InsertExport.Literal(fine, value, insert);
        InsertExport.Literal(plain, value, insert);
        Assert.Equal([expected], insert.ToList());

        var csv = new ExportWarnings();
        ExportNotation.Text(fine, new DateTimeOffset(value, TimeSpan.FromHours(2)), csv);
        Assert.Equal([expected], csv.ToList());
        Assert.Empty(csv.ToList(fractions: false)); // the IN list words it itself
    }
}
