using FerretSharp.Core.Query;
using FerretSharp.Core.Schema;

namespace FerretSharp.Core.Tests.Query;

public class FilterRulesTests
{
    [Theory]
    [InlineData("1234.5", 1234.5)]
    [InlineData("1234,5", 1234.5)]
    [InlineData("1.234,5", 1234.5)]
    [InlineData("-0.25", -0.25)]
    [InlineData(" 42 ", 42)]
    public void Parses_numbers_in_german_and_invariant_notation(string text, double expected)
    {
        Assert.True(FilterRules.TryParseNumber(text, out var value));
        Assert.Equal((decimal)expected, value);
    }

    [Theory]
    [InlineData("1.234")] // ambiguous without comma → invariant decimal point, not thousands
    public void Dot_without_comma_is_a_decimal_point(string text)
    {
        Assert.True(FilterRules.TryParseNumber(text, out var value));
        Assert.Equal(1.234m, value);
    }

    [Theory]
    [InlineData("1.10.2026", 2026, 10, 1, 0, 0, true)]
    [InlineData("01.10.2026 14:30", 2026, 10, 1, 14, 30, false)]
    [InlineData("2026-10-01", 2026, 10, 1, 0, 0, true)]
    [InlineData("2026-10-01T08:15:00", 2026, 10, 1, 8, 15, false)]
    [InlineData("01.10.2026 14:30:00,0", 2026, 10, 1, 14, 30, false)]
    public void Parses_dates_and_detects_date_only(string text, int y, int m, int d, int h, int min, bool dateOnly)
    {
        Assert.True(FilterRules.TryParseDate(text, out var value, out var isDateOnly));
        Assert.Equal(new DateTime(y, m, d, h, min, 0), value);
        Assert.Equal(dateOnly, isDateOnly);
    }

    [Fact]
    public void Operators_depend_on_the_column_category()
    {
        Assert.Contains(FilterOperator.Contains, FilterRules.OperatorsFor(ColumnCategory.Text));
        Assert.DoesNotContain(FilterOperator.Contains, FilterRules.OperatorsFor(ColumnCategory.Number));
        Assert.DoesNotContain(FilterOperator.Contains, FilterRules.OperatorsFor(ColumnCategory.Date));
        Assert.Equal([FilterOperator.IsNull, FilterOperator.IsNotNull], FilterRules.OperatorsFor(ColumnCategory.Blob));
        Assert.Equal(FilterOperator.Contains, FilterRules.OperatorsFor(ColumnCategory.Text)[0]);
    }

    [Theory]
    [InlineData("VARCHAR2", ColumnCategory.Text)]
    [InlineData("NUMBER", ColumnCategory.Number)]
    [InlineData("TIMESTAMP(6)", ColumnCategory.Timestamp)]
    [InlineData("TIMESTAMP(6) WITH TIME ZONE", ColumnCategory.TimestampWithTimeZone)]
    [InlineData("TIMESTAMP(6) WITH LOCAL TIME ZONE", ColumnCategory.TimestampWithTimeZone)]
    [InlineData("INTERVAL DAY(2) TO SECOND(6)", ColumnCategory.Interval)]
    [InlineData("NCLOB", ColumnCategory.Clob)]
    [InlineData("LONG RAW", ColumnCategory.Long)]
    [InlineData("XMLTYPE", ColumnCategory.Unsupported)]
    [InlineData("SDO_GEOMETRY", ColumnCategory.Unsupported)]
    public void Categorizes_oracle_types(string dataType, ColumnCategory expected)
    {
        Assert.Equal(expected, ColumnCategories.Of(dataType));
    }
}
