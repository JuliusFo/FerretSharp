using FerretSharp.Core.ClrModel;
using FerretSharp.Core.Data;
using FerretSharp.Core.Display;
using FerretSharp.Core.Query;
using FerretSharp.Core.Schema;
using FerretSharp.Core.Settings;
using FerretSharp.Core.Tests.ClrModel;

namespace FerretSharp.Core.Tests.Data;

/// <summary>WP-53: typed cells, what Excel would change as text, header, filter and widths, the presented variant.</summary>
public class XlsxExportTests
{
    private static ColumnInfo Col(string name, string type, int? scale = null) =>
        new(name, type, null, false, null, scale, true, false, null, 0);

    private static readonly TableDetails Table = new(
        new TableSummary("APP", "KUNDEN", TableKind.Table),
        [
            Col("ID", "NUMBER", 0), Col("NAME", "VARCHAR2"), Col("BETRAG", "NUMBER", 2), Col("DATUM", "DATE"), Col("TS", "TIMESTAMP(6)"),
            Col("GUID", "RAW"), Col("NOTIZ", "CLOB"), Col("BILD", "BLOB"), Col("TSTZ", "TIMESTAMP(6) WITH TIME ZONE"), Col("AKTIV", "BOOLEAN"),
        ],
        ["ID"], [], false);

    private static RowData Row(params object?[] values) => new(RowKey.None.Instance, values);

    /// <summary>The rows written at once, as the export of the selected rows writes them (row count known).</summary>
    private static async Task<(ExportSummary File, XlsxSheet Sheet)> ExportAsync(IReadOnlyList<ExportColumn> columns, params RowData[] rows)
    {
        var ct = TestContext.Current.CancellationToken;
        using var stream = new MemoryStream();
        var summary = await RowExport.WriteAsync(RowExport.FromRows(rows), await XlsxExport.CreateWriterAsync(stream, columns, "KUNDEN", rows.Length, ct), null, null, ct);
        return (summary, XlsxSheet.Read(stream.ToArray()));
    }

    [Fact]
    public async Task Values_are_typed_cells_and_null_is_empty()
    {
        var (file, sheet) = await ExportAsync(ExportColumn.Raw(Table),
            Row(1234m, "=SUMME(A1:A2)", 1234.5m, new DateTime(2026, 10, 1, 13, 45, 7), new DateTime(2026, 10, 1, 13, 45, 7, 123),
                new byte[] { 0xCA, 0xFE }, new LobValue("kurz", 4), null, new DateTimeOffset(2026, 10, 1, 13, 45, 7, TimeSpan.FromHours(2)), true));

        Assert.Equal(1, file.Rows);
        Assert.Empty(file.Warnings);
        Assert.Equal(["ID", "NAME", "BETRAG", "DATUM", "TS", "GUID", "NOTIZ", "BILD", "TSTZ", "AKTIV"],
            "ABCDEFGHIJ".Select(c => sheet[c + "1"].Value));
        Assert.Equal(new XlsxCell("n", "1234", "0"), sheet["A2"]);
        Assert.True(sheet["B2"].IsText); // a text is never a formula
        Assert.Equal("=SUMME(A1:A2)", sheet["B2"].Value);
        Assert.Equal(new XlsxCell("n", "1234.5", null), sheet["C2"]);
        Assert.Equal("dd.mm.yyyy hh:mm:ss", sheet["D2"].Format);
        Assert.Equal(46296.573, Math.Round(double.Parse(sheet["D2"].Value!, System.Globalization.CultureInfo.InvariantCulture), 3));
        Assert.Equal("dd.mm.yyyy hh:mm:ss.000", sheet["E2"].Format);
        Assert.Equal("CAFE", sheet["F2"].Value);
        Assert.Equal("kurz", sheet["G2"].Value);
        Assert.False(sheet.Cells.TryGetValue("H2", out var empty) && empty.Value is { Length: > 0 });
        Assert.True(sheet["I2"].IsText);
        Assert.Equal("01.10.2026 13:45:07 +02:00", sheet["I2"].Value);
        Assert.Equal(new XlsxCell("b", "1", null), sheet["J2"]);
    }

    [Theory]
    [InlineData("123456789012345", true)] // 15 digits: Excel keeps them
    [InlineData("1234567890123456", false)]
    [InlineData("0.000000000012345", true)]
    [InlineData("1000000000000000000", true)] // one significant digit
    [InlineData("1234567890.123456", false)]
    public async Task Numbers_with_more_than_15_significant_digits_become_text(string invariant, bool number)
    {
        var value = decimal.Parse(invariant, System.Globalization.CultureInfo.InvariantCulture);

        var (_, sheet) = await ExportAsync([new ExportColumn("N", 0, Col("N", "NUMBER"))], Row(value));

        Assert.Equal(number, !sheet["A2"].IsText);
        if (!number)
        {
            Assert.Equal(value.ToString(System.Globalization.CultureInfo.GetCultureInfo("de-DE")), sheet["A2"].Value);
        }
    }

    [Fact]
    public async Task Big_numbers_and_long_keys_beyond_double_precision_become_text()
    {
        var (_, sheet) = await ExportAsync([new ExportColumn("N", 0, Col("N", "NUMBER")), new ExportColumn("L", 1, Col("L", "NUMBER"))],
            Row(new BigNumber("12345678901234567890123456789012345678.5"), 9007199254740993L),
            Row(null, 42L));

        Assert.Equal("12345678901234567890123456789012345678,5", sheet["A2"].Value);
        Assert.True(sheet["A2"].IsText);
        Assert.Equal("9007199254740993", sheet["B2"].Value);
        Assert.True(sheet["B2"].IsText);
        Assert.Equal(new XlsxCell("n", "42", "0"), sheet["B3"]);
    }

    [Fact]
    public async Task Timestamps_finer_than_milliseconds_and_dates_before_1900_become_text()
    {
        var columns = new[] { new ExportColumn("TS", 0, Col("TS", "TIMESTAMP(9)")), new ExportColumn("D", 1, Col("D", "DATE")) };

        var (_, sheet) = await ExportAsync(columns,
            Row(new DateTime(2026, 10, 1, 13, 45, 7).AddTicks(1234567), new DateTime(1899, 12, 31)),
            Row(new DateTime(2026, 10, 1, 13, 45, 7, 120), new DateTime(1900, 3, 1)));

        Assert.Equal("01.10.2026 13:45:07.1234567", sheet["A2"].Value);
        Assert.Equal("31.12.1899 00:00:00", sheet["B2"].Value);
        Assert.True(sheet["A2"].IsText && sheet["B2"].IsText);
        Assert.Equal("n", sheet["A3"].Type);
        Assert.Equal("n", sheet["B3"].Type);
        Assert.Equal("61", sheet["B3"].Value); // 1 March 1900, after Excel's 29 February 1900
    }

    [Fact]
    public async Task Doubles_floats_and_special_values()
    {
        var columns = new[] { new ExportColumn("D", 0, Col("D", "BINARY_DOUBLE")), new ExportColumn("F", 1, Col("F", "BINARY_FLOAT")) };

        var (_, sheet) = await ExportAsync(columns, Row(0.1d, 0.1f), Row(double.NaN, float.PositiveInfinity));

        Assert.Equal(new XlsxCell("n", "0.1", null), sheet["A2"]);
        Assert.Equal(new XlsxCell("n", "0.1", null), sheet["B2"]); // not 0.100000001490116
        Assert.Equal("NaN", sheet["A3"].Value);
        Assert.True(sheet["B3"].IsText);
    }

    /// <summary>R6b, B6: what Excel gets as text is written in the notation of the CSV export, from one place.</summary>
    [Theory]
    [MemberData(nameof(TextValues))]
    public async Task Values_written_as_text_have_the_notation_of_the_csv_export(string type, object value)
    {
        var column = Col("X", type);

        var (_, sheet) = await ExportAsync([new ExportColumn("X", 0, column)], Row(value));

        Assert.True(sheet["A2"].IsText);
        Assert.Equal(DelimitedExport.CellText(column, value).Text, sheet["A2"].Value);
    }

    public static TheoryData<string, object> TextValues() => new()
    {
        { "NUMBER", new BigNumber("12345678901234567890123456789012345678.5") },
        { "NUMBER", 9007199254740993L },
        { "NUMBER", 1234567890.123456m },
        { "TIMESTAMP(6) WITH TIME ZONE", new DateTimeOffset(2026, 10, 1, 13, 45, 7, TimeSpan.FromHours(2)) },
        { "TIMESTAMP(9)", new DateTime(2026, 10, 1, 13, 45, 7).AddTicks(1234567) },
        { "DATE", new DateTime(1899, 12, 31, 8, 0, 0) },
        { "RAW", new byte[] { 0xCA, 0xFE } },
        { "BINARY_DOUBLE", double.NaN },
        { "BINARY_FLOAT", float.PositiveInfinity },
        { "INTERVAL DAY(2) TO SECOND(6)", TimeSpan.FromHours(26) },
        { "VARCHAR2", "Text" },
    };

    [Fact]
    public async Task Incomplete_lobs_are_left_out_and_long_texts_shortened_with_warnings()
    {
        var huge = new string('x', XlsxExport.MaxCellText + 10);

        var (file, sheet) = await ExportAsync(ExportColumn.Raw(Table),
            Row(1m, huge, null, null, null, null, new LobValue(new string('y', 200), 5000), new LobValue(null, 10), null, null),
            Row(2m, huge, null, null, null, null, new LobValue(huge, huge.Length), null, null, null));

        Assert.Equal(XlsxExport.MaxCellText, sheet["B2"].Value!.Length);
        Assert.Equal(XlsxExport.MaxCellText, sheet["G3"].Value!.Length);
        Assert.Equal(
            [
                "NOTIZ (CLOB): 1 Wert nicht exportiert – nur die Vorschau geladen.",
                "BILD (BLOB): 1 Wert nicht exportiert – nur die Länge geladen.",
                "NAME (VARCHAR2): 2 Werte gekürzt – eine Excel-Zelle fasst höchstens 32.767 Zeichen.",
                "NOTIZ (CLOB): 1 Wert gekürzt – eine Excel-Zelle fasst höchstens 32.767 Zeichen.",
            ],
            file.Warnings);
    }

    [Fact]
    public async Task Header_is_bold_and_frozen_with_auto_filter_and_widths_by_content()
    {
        var columns = new[] { new ExportColumn("ID", 0, Col("ID", "NUMBER")), new ExportColumn("NAME", 1, Col("NAME", "VARCHAR2")) };

        var (_, sheet) = await ExportAsync(columns, Row(1m, "kurz"), Row(2m, new string('x', 200) + "\nzweite Zeile"), Row(3m, "mittellanger Name"));

        Assert.Equal("A2", sheet.FrozenAt);
        Assert.Equal("A1:B4", sheet.AutoFilter);
        Assert.Equal([XlsxExport.MinWidth, XlsxExport.MaxWidth], sheet.Widths);
        Assert.Equal("KUNDEN", sheet.Name);
    }

    [Theory]
    [InlineData("KUNDEN", "KUNDEN")]
    [InlineData("A/B:C*D?[E]\\F", "A_B_C_D__E__F")]
    [InlineData("'quoted'", "quoted")]
    [InlineData("SEHR_LANGER_TABELLENNAME_MIT_UEBERLAENGE", "SEHR_LANGER_TABELLENNAME_MIT_UE")]
    [InlineData("   ", "Export")]
    public void Sheet_names_are_made_valid(string name, string expected) => Assert.Equal(expected, XlsxExport.SheetName(name));

    [Theory]
    [InlineData(1, "A")]
    [InlineData(26, "Z")]
    [InlineData(27, "AA")]
    [InlineData(703, "AAA")]
    public void Column_letters(int column, string letters) => Assert.Equal(letters, XlsxExport.ColumnLetters(column));

    [Fact]
    public async Task Presented_columns_show_property_names_members_and_looked_up_names()
    {
        var presentation = await CodeGenerationModel.PresentationAsync();
        var kunden = CodeGenerationModel.Kunden;
        var mandant = new TableDetails(new TableSummary("APP", "MANDANT", TableKind.Table), [Col("ID", "NUMBER"), Col("NAME", "VARCHAR2")], ["ID"], [], false);
        var link = new DisplayLink(CodeGenerationModel.Index("MANDANT_ID"),
            new ForeignKeyInfo("FK_MANDANT", kunden.Table.Ref, ["MANDANT_ID"], mandant.Table.Ref, ["ID"], FkSource.Declared), mandant, 0, [new DisplayPart(1)]);
        var names = new DisplayNames([link], (_, _, value) => value as string);
        var lookup = Assert.Single(names.Missing([[1m, null, null, null, null, null, null, null, 7m, null, null, null]]));
        names.Found(lookup, [new LookupRow(7m, ["Hauptmandant"])]);

        var columns = PresentedExport.Columns(presentation, names);
        var row = Row(1m, "Peter", new DateTime(2026, 1, 2), 10.5m, "J", 2m, "AKTIV", null, 7m, null, "X", null);
        var (_, sheet) = await ExportAsync(columns, row);

        Assert.True(ExportColumn.DiffersFromRaw(columns));
        Assert.Equal(
            ["KundeId", "Name", "ErstelltAm", "Umsatz", "Gesperrt", "Kundenart", "Status", "LEGACY_CODE", "MandantId", "MandantId · NAME", "ExternId", "Geprueft", "Notiz"],
            columns.Select(c => c.Header));
        Assert.Equal(new XlsxCell("b", "1", null), sheet["E2"]); // J → true
        Assert.Equal("Gewerbe (2)", sheet["F2"].Value);
        Assert.Equal("Aktiv (AKTIV)", sheet["G2"].Value);
        Assert.Equal(new XlsxCell("n", "7", "0"), sheet["I2"]); // the key stays a number
        Assert.Equal("Hauptmandant", sheet["J2"].Value);
        Assert.Equal("X", sheet["L2"].Value); // no member: the value itself
    }

    [Fact]
    public async Task Without_model_the_presented_columns_are_the_raw_ones()
    {
        var columns = PresentedExport.Columns(TablePresentation.Plain(Table), DisplayNames.None);
        var presentedOff = PresentedExport.Columns(TablePresentation.Create(CodeGenerationModel.Kunden, await CodeGenerationModel.MappingAsync(), ClrNameDisplay.Off), DisplayNames.None);

        Assert.False(ExportColumn.DiffersFromRaw(columns));
        Assert.Equal(CodeGenerationModel.Kunden.Columns.Select(c => c.Name), presentedOff.Select(c => c.Header));
        Assert.True(ExportColumn.DiffersFromRaw(presentedOff)); // C# names off, members still by name
    }
}
