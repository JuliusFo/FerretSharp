using FerretSharp.Core.Data;
using FerretSharp.Core.Query;
using FerretSharp.Core.Schema;

namespace FerretSharp.Core.Tests.Query;

public class ColumnValuesTests
{
    private static ColumnInfo Col(string name, string type, int? precision = null, int? scale = null) =>
        new(name, type, null, false, precision, scale, true, false, null, 0);

    private static readonly ColumnInfo Name = Col("NAME", "VARCHAR2");
    private static readonly ColumnInfo Amount = Col("BETRAG", "NUMBER", 10, 2);
    private static readonly ColumnInfo Created = Col("ERSTELLT_AM", "DATE");
    private static readonly ColumnInfo National = Col("TITEL", "NVARCHAR2");

    private static ColumnValuesText Copy(ColumnInfo column, ColumnValuesFormat format, params object?[] values) =>
        Copy(new ExportColumn(column.Name, 0, column), format, values);

    private static ColumnValuesText Copy(ExportColumn column, ColumnValuesFormat format, params object?[] values)
    {
        var copy = new ColumnValues(column, format);
        Assert.True(copy.AddRange(values));
        return copy.Finish();
    }

    [Fact]
    public void Lines_hold_one_value_each_in_order_with_null_as_an_empty_line()
    {
        var text = Copy(Name, ColumnValuesFormat.Lines, "B", null, "A", "B");

        Assert.Equal("B\r\n\r\nA\r\nB", text.Text);
        Assert.Equal(4, text.Values);
        Assert.Empty(text.Warnings);
    }

    [Fact]
    public void Lines_use_the_notation_of_ctrl_c()
    {
        Assert.Equal("1234,5\r\n0,25", Copy(Amount, ColumnValuesFormat.Lines, 1234.5m, 0.25m).Text);
        Assert.Equal("01.10.2026 14:30:00", Copy(Created, ColumnValuesFormat.Lines, new DateTime(2026, 10, 1, 14, 30, 0)).Text);
    }

    [Fact]
    public void A_line_with_a_line_break_tab_or_quote_is_quoted_like_excel_reads_it()
    {
        Assert.Equal("\"zwei\nZeilen\"\r\n\"sagt \"\"hallo\"\"\"\r\n\"a\tb\"\r\nschlicht",
            Copy(Name, ColumnValuesFormat.Lines, "zwei\nZeilen", "sagt \"hallo\"", "a\tb", "schlicht").Text);
    }

    [Fact]
    public void Lines_show_what_the_presentation_shows()
    {
        var column = new ExportColumn("Art", 0, Amount) { Present = value => value is 2m ? "Gewerbe (2)" : value };

        Assert.Equal("Gewerbe (2)\r\n7", Copy(column, ColumnValuesFormat.Lines, 2m, 7m).Text);
    }

    [Fact]
    public void The_in_list_holds_database_literals_each_once_without_null()
    {
        var column = new ExportColumn("Art", 0, Amount) { Present = _ => "Gewerbe (2)" };

        var text = Copy(column, ColumnValuesFormat.InList, 2m, null, 7.5m, 2m, null);

        Assert.Equal("2, 7.5", text.Text);
        Assert.Equal(2, text.Values);
        Assert.Equal(["2 NULL-Werte stehen nicht in der Liste: IN findet NULL nie."], text.Warnings);
    }

    [Fact]
    public void The_in_list_quotes_texts_and_writes_dates_and_national_texts_as_oracle_reads_them()
    {
        Assert.Equal("'O''Brien', 'Müller'", Copy(Name, ColumnValuesFormat.InList, "O'Brien", "Müller").Text);
        Assert.Equal("N'Ωmega'", Copy(National, ColumnValuesFormat.InList, "Ωmega").Text);
        Assert.Equal("TO_DATE('2026-10-01 14:30:00', 'YYYY-MM-DD HH24:MI:SS')", Copy(Created, ColumnValuesFormat.InList, new DateTime(2026, 10, 1, 14, 30, 0)).Text);
    }

    [Fact]
    public void Nulls_collapsed_by_distinct_are_named_without_a_number()
    {
        var copy = new ColumnValues(new ExportColumn("NAME", 0, Name), ColumnValuesFormat.InList) { NullsCollapsed = true };
        copy.AddRange(["A", null]);

        Assert.Equal(["NULL-Werte stehen nicht in der Liste: IN findet NULL nie."], copy.Finish().Warnings);
    }

    [Theory]
    [InlineData("TIMESTAMP(9)", 9, true)]
    [InlineData("TIMESTAMP(9) WITH TIME ZONE", 9, true)]
    [InlineData("TIMESTAMP(6)", 6, false)]
    [InlineData("TIMESTAMP(7)", 7, false)]
    public void An_in_list_of_finer_timestamps_than_datetime_holds_says_so(string type, int scale, bool warns)
    {
        var text = Copy(Col("ZEIT", type, scale: scale), ColumnValuesFormat.InList, new DateTime(2026, 10, 1, 12, 0, 0));

        Assert.Equal(warns, text.Warnings.Any(w => w.Contains(type, StringComparison.Ordinal)));
    }

    [Fact]
    public void An_in_list_longer_than_oracle_takes_says_so()
    {
        var text = Copy(Amount, ColumnValuesFormat.InList, [.. Enumerable.Range(1, 1001).Select(i => (object?)(decimal)i)]);

        Assert.Equal(1001, text.Values);
        Assert.Contains(text.Warnings, w => w.Contains("ORA-01795", StringComparison.Ordinal) && w.Contains($"{QueryBuilder.MaxInListSize:N0}", StringComparison.Ordinal));
        Assert.DoesNotContain(Copy(Amount, ColumnValuesFormat.InList, [.. Enumerable.Range(1, 1000).Select(i => (object?)(decimal)i)]).Warnings,
            w => w.Contains("ORA-01795", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(ColumnValuesFormat.Lines)]
    [InlineData(ColumnValuesFormat.InList)]
    public void More_values_than_the_limit_stop_the_reading_and_copy_nothing(ColumnValuesFormat format)
    {
        var copy = new ColumnValues(new ExportColumn("BETRAG", 0, Amount), format);

        Assert.True(copy.AddRange(Enumerable.Range(1, ColumnValues.MaxValues).Select(i => (object?)(decimal)i)));
        Assert.False(copy.TooMany);
        Assert.False(copy.Add(0m));
        Assert.True(copy.TooMany);
        Assert.False(copy.Add(1m));
        Assert.Throws<InvalidOperationException>(copy.Finish);
    }

    [Fact]
    public void Duplicates_do_not_count_towards_the_limit_of_an_in_list()
    {
        var copy = new ColumnValues(new ExportColumn("NAME", 0, Name), ColumnValuesFormat.InList);

        Assert.True(copy.AddRange(Enumerable.Repeat<object?>("A", ColumnValues.MaxValues + 10)));
        Assert.Equal(1, copy.Count);
    }

    [Theory]
    [InlineData("VARCHAR2", true)]
    [InlineData("NUMBER", true)]
    [InlineData("RAW", true)]
    [InlineData("CLOB", false)]
    [InlineData("BLOB", false)]
    [InlineData("LONG", false)]
    [InlineData("XMLTYPE", false)]
    public void Only_columns_read_as_values_can_be_copied(string type, bool copyable)
    {
        Assert.Equal(copyable, ColumnValues.CanCopy(Col("C", type)));
    }
}
