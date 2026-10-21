using FerretSharp.Core.ClrModel;
using FerretSharp.Core.Data;
using FerretSharp.Core.Query;
using FerretSharp.Core.Schema;
using FerretSharp.Core.Settings;
using NSubstitute;

namespace FerretSharp.Core.Tests.ClrModel;

public sealed class CSharpCodeTests
{
    private static PropertyExport Of(string type, bool nullable = false) =>
        new("P", type, nullable, false, "C", null, null, null, false, null);

    private static readonly ColumnInfo Number = new("C", "NUMBER", null, false, null, null, true, false, null, 1);
    private static readonly ColumnInfo Text = new("C", "VARCHAR2", 100, true, null, null, true, false, null, 1);
    private static readonly ColumnInfo Date = new("C", "DATE", null, false, null, null, true, false, null, 1);
    private static readonly ColumnInfo Timestamp = new("C", "TIMESTAMP(7)", null, false, null, 7, true, false, null, 1);
    private static readonly ColumnInfo Raw = new("C", "RAW", 16, false, null, null, true, false, null, 1);

    private static string? Code(string type, ColumnInfo column, object? raw) => CSharpCode.Value(Of(type), null, column, raw).Code;

    [Fact]
    public void Numbers_take_the_property_type()
    {
        Assert.Equal("4711", Code("int", Number, 4711m));
        Assert.Equal("12345678901", Code("long", Number, 12345678901m));
        Assert.Equal("1234.50m", Code("decimal", Number, 1234.50m));
        Assert.Equal("-3m", Code("decimal", Number, -3m));
        Assert.Equal("1.5", Code("double", Number, 1.5m));
        Assert.Equal("double.NaN", Code("double", Number, double.NaN));
        Assert.Equal("0.25f", Code("float", Number, 0.25f));
        Assert.Equal("true", Code("bool", Number, 1m));
        Assert.Null(Code("int", Number, 1.5m));
        Assert.Null(Code("byte", Number, 300m));
        Assert.Equal("null", Code("int", Number, null));
    }

    [Fact]
    public void Texts_are_escaped()
    {
        Assert.Equal("\"Meier \\\"GmbH\\\"\\r\\nZeile 2\\t\\\\\"", Code("string", Text, "Meier \"GmbH\"\r\nZeile 2\t\\"));
        Assert.Equal("\"Grüße\"", Code("string", Text, "Grüße"));
        Assert.Equal("\"\\u0001\"", Code("string", Text, "\u0001"));
        Assert.Equal("'\\''", Code("char", Text, "'"));
    }

    [Fact]
    public void Dates_use_constructors_only_as_precise_as_needed()
    {
        Assert.Equal("new DateTime(2026, 10, 5)", Code("DateTime", Date, new DateTime(2026, 10, 5)));
        Assert.Equal("new DateTime(2026, 10, 5, 14, 2, 13)", Code("DateTime", Date, new DateTime(2026, 10, 5, 14, 2, 13)));
        Assert.Equal("new DateTime(2026, 10, 5, 14, 2, 13, 120)", Code("DateTime", Timestamp, new DateTime(2026, 10, 5, 14, 2, 13, 120)));
        Assert.Equal("new DateTime(2026, 10, 5, 0, 0, 0, 0, 5)", Code("DateTime", Timestamp, new DateTime(2026, 10, 5).AddTicks(50)));
        Assert.Equal("new DateTime(2026, 10, 5, 0, 0, 0, 0, 5).AddTicks(3)", Code("DateTime", Timestamp, new DateTime(2026, 10, 5).AddTicks(53)));
        Assert.Equal("new DateOnly(2026, 10, 5)", Code("DateOnly", Date, new DateTime(2026, 10, 5)));
        Assert.Equal("new DateTimeOffset(2026, 10, 5, 14, 0, 0, TimeSpan.FromHours(2))",
            Code("DateTimeOffset", Timestamp, new DateTimeOffset(2026, 10, 5, 14, 0, 0, TimeSpan.FromHours(2))));
        Assert.Equal("new DateTimeOffset(2026, 10, 5, 14, 0, 0, new TimeSpan(5, 30, 0))",
            Code("DateTimeOffset", Timestamp, new DateTimeOffset(2026, 10, 5, 14, 0, 0, new TimeSpan(5, 30, 0))));
        Assert.Equal("new TimeSpan(1, 2, 3, 4, 500)", Code("TimeSpan", Text, "+000000001 02:03:04.500000000"));
        Assert.Equal("new TimeSpan(2, 0, 0)", Code("TimeSpan", Text, "+00 02:00:00.000000"));
    }

    [Fact]
    public void Guids_read_raw_16_in_dotnet_byte_order()
    {
        var guid = Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e");

        Assert.Equal("new Guid(\"0f8fad5b-d9cb-469f-a165-70867728950e\")", Code("Guid", Raw, guid.ToByteArray()));
        Assert.Equal("new Guid(\"0f8fad5b-d9cb-469f-a165-70867728950e\")", Code("Guid", Text, "0F8FAD5B-D9CB-469F-A165-70867728950E"));
        Assert.Equal("Convert.FromHexString(\"CAFE01\")", Code("byte[]", Raw, new byte[] { 0xCA, 0xFE, 0x01 }));
    }

    [Fact]
    public void Enums_by_member_flags_combined_numbers_without_member_cast()
    {
        var kundenart = new PropertyExport("Kundenart", "Kundenart", false, false, "C", null, null, "int", false,
            [new("Privat", "1", "1"), new("Gewerbe", "2", "2")]);
        var values = ValueTable.For(kundenart, Number);
        Assert.Equal("Kundenart.Gewerbe", CSharpCode.Value(kundenart, values, Number, 2m).Code);
        Assert.Equal("(Kundenart)7", CSharpCode.Value(kundenart, values, Number, 7m).Code);
        Assert.Equal("(Kundenart)(-1)", CSharpCode.Value(kundenart, values, Number, -1m).Code);

        var rechte = new PropertyExport("Rechte", "Rechte", false, false, "C", null, null, "int", true,
            [new("Keine", "0", "0"), new("Lesen", "1", "1"), new("Schreiben", "2", "2")]);
        Assert.Equal("Rechte.Lesen | Rechte.Schreiben", CSharpCode.Value(rechte, ValueTable.For(rechte, Number), Number, 3m).Code);

        var status = new PropertyExport("Status", "KundeStatus", false, false, "C", null, "UpperCaseEnumConverter", "string",
            false, [new("Aktiv", "0", "AKTIV")]);
        var unknown = CSharpCode.Value(status, ValueTable.For(status, Text), Text, "WEG");
        Assert.Null(unknown.Code);
        Assert.Equal("'WEG' ist kein Wert von KundeStatus", unknown.Problem);
    }

    [Fact]
    public void Other_converters_and_partly_loaded_lobs_have_no_literal()
    {
        var converted = Of("string") with { Converter = "TrimConverter" };
        Assert.Equal("eigener Converter TrimConverter, DB-Wert 'x'", CSharpCode.Value(converted, null, Text, "x").Problem);

        var clob = new ColumnInfo("C", "CLOB", null, false, null, null, true, false, null, 1);
        Assert.Equal("\"kurz\"", Code("string", clob, new LobValue("kurz", 4)));
        Assert.Equal("CLOB nur als Vorschau geladen (5.000 Zeichen)", CSharpCode.Value(Of("string"), null, clob, new LobValue("…", 5000)).Problem);
    }

    [Fact]
    public void Names_avoid_keywords() => Assert.Equal(["@class", "kunde", "@event"],
        [CSharpCode.Identifier("class"), CSharpCode.LocalName("Kunde"), CSharpCode.LocalName("Event")]);
}
