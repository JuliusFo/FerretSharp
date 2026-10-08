using FerretSharp.Core.Data;
using FerretSharp.Core.Oracle;
using FerretSharp.Core.Schema;

namespace FerretSharp.Core.Tests.Data;

public class CellFormatterTests
{
    private static ColumnInfo Col(string type, int? scale = null) => new("C", type, null, false, null, scale, true, false, null, 1);

    [Theory]
    [InlineData("1234567.5", 2, "1.234.567,50")]
    [InlineData("-1234", 0, "-1.234")]
    [InlineData("0.125", null, "0,125")]
    [InlineData("123456789012345678901234567890123456", 0, "123.456.789.012.345.678.901.234.567.890.123.456")]
    [InlineData("999", null, "999")]
    public void Formats_numbers_without_precision_loss(string invariant, int? scale, string expected)
    {
        Assert.Equal(expected, CellFormatter.FormatNumber(invariant, scale));
    }

    [Fact]
    public void Null_stays_null_and_empty_text_stays_empty()
    {
        Assert.Null(CellFormatter.Format(Col("VARCHAR2"), null));
        Assert.Equal("", CellFormatter.Format(Col("VARCHAR2"), ""));
    }

    [Fact]
    public void Dates_and_timestamps_use_german_notation_with_fraction_digits()
    {
        var value = new DateTime(2026, 10, 1, 14, 30, 5).AddTicks(1234567);

        Assert.Equal("01.10.2026 14:30:05", CellFormatter.Format(Col("DATE"), value));
        Assert.Equal("01.10.2026 14:30:05,123", CellFormatter.Format(Col("TIMESTAMP(3)", scale: 3), value));
        Assert.Equal("01.10.2026 14:30:05 +02:00",
            CellFormatter.Format(Col("DATE"), new DateTimeOffset(new DateTime(2026, 10, 1, 14, 30, 5), TimeSpan.FromHours(2))));
    }

    [Fact]
    public void Lobs_raw_and_unsupported_types_get_placeholders()
    {
        Assert.Equal("Hallo ⏎ Welt …", CellFormatter.Format(Col("CLOB"), new LobValue("Hallo\r\nWelt", 5000)));
        Assert.Equal("‹leer›", CellFormatter.Format(Col("CLOB"), new LobValue("", 0)));
        Assert.Equal("‹BLOB 1,5 KB›", CellFormatter.Format(Col("BLOB"), new LobValue(null, 1536)));
        Assert.Equal("CAFE", CellFormatter.Format(Col("RAW"), new byte[] { 0xCA, 0xFE }));
        Assert.Equal("‹XMLTYPE›", CellFormatter.Format(Col("XMLTYPE"), new NotNullMarker("XMLTYPE")));
    }

    [Theory]
    [InlineData("12.5", typeof(decimal))]
    [InlineData("1234567890123456789012345678", typeof(decimal))]
    [InlineData("123456789012345678901234567890123456", typeof(BigNumber))]
    [InlineData("0.12345678901234567890123456789012", typeof(BigNumber))]
    [InlineData("0.0000000000000000000000000000001", typeof(BigNumber))] // one digit, but 31 places: decimal would round to 0
    [InlineData("-0.0000000000000000000000000001", typeof(decimal))]
    [InlineData("1E-130", typeof(BigNumber))]
    public void Numbers_beyond_decimal_precision_stay_exact(string invariant, Type expected)
    {
        Assert.IsType(expected, OracleDataAccess.ToNumber(invariant));
    }

    [Theory]
    [InlineData("1,5", "1.5")]
    [InlineData("-0,25", "-0.25")]
    [InlineData("1.5", "1.5")]
    [InlineData("42", "42")]
    public void Culture_formatted_oracle_decimals_are_normalized(string text, string expected)
    {
        Assert.Equal(expected, OracleDataAccess.NormalizeDecimalSeparator(text));
    }
}
