using FerretSharp.Core.Data;
using FerretSharp.Core.Query;
using FerretSharp.Core.Schema;

namespace FerretSharp.Core.Tests.Query;

/// <summary>Text pasted into a list filter (found in the E2E of WP-58): lines and IN lists become its values.</summary>
public class PastedListTests
{
    [Theory]
    [InlineData("'041', '054', '067'", "041; 054; 067")]
    [InlineData("('A', 'B')", "A; B")]
    [InlineData("'O''Brien', N'Ωmega'", "O'Brien; Ωmega")]
    [InlineData("'A',\r\n'B',\r\n'C'", "A; B; C")] // an IN list over several lines
    [InlineData("2, 7.5, -3, 1E3", "2; 7.5; -3; 1E3")]
    [InlineData("'A', NULL, 'B'", "A; B")]
    [InlineData("'041'", "041")] // one quoted value: without the quotes
    [InlineData("TO_DATE('2026-10-01 14:30:00', 'YYYY-MM-DD HH24:MI:SS'), TO_DATE('2026-10-02 00:00:00', 'YYYY-MM-DD HH24:MI:SS')",
        "2026-10-01 14:30:00; 2026-10-02 00:00:00")]
    [InlineData("HEXTORAW('0A0B'), HEXTORAW('FF')", "0A0B; FF")]
    public void An_in_list_becomes_the_filter_s_values(string pasted, string expected) =>
        Assert.Equal(expected, PastedList.ToFilterValue(pasted));

    [Theory]
    [InlineData("A\r\nB\r\n\r\nC\r\n", "A; B; C")]
    [InlineData("  041 \n 054 ", "041; 054")]
    [InlineData("\"sagt \"\"hallo\"\"\"\r\nschlicht", "sagt \"hallo\"; schlicht")]
    [InlineData("1,5\r\n2,25", "1,5; 2,25")] // German numbers in lines stay as they are
    public void Lines_become_the_filter_s_values(string pasted, string expected) =>
        Assert.Equal(expected, PastedList.ToFilterValue(pasted));

    [Theory]
    [InlineData("Müller")]
    [InlineData("42")]
    [InlineData("1,5")] // a German decimal number, not two values
    [InlineData("1,5, 2,5")]
    [InlineData("A; B")] // already the filter's notation
    [InlineData("'A', 'B")] // not closed
    [InlineData("'A' 'B'")]
    [InlineData("'A',")]
    [InlineData("A\tB\r\nC\tD")] // a range of several columns
    [InlineData("'A;B', 'C'")] // a value with the separator cannot be a list value
    [InlineData("")]
    public void Anything_else_is_pasted_as_it_is(string pasted) =>
        Assert.Null(PastedList.ToFilterValue(pasted));

    [Fact]
    public void What_copying_a_column_gives_comes_back_as_its_values()
    {
        var column = new ColumnInfo("KUERZEL", "CHAR", 3, false, null, null, true, false, null, 0);
        object?[] values = ["041", "054", null, "067"];
        var inList = new ColumnValues(new ExportColumn("KUERZEL", 0, column), ColumnValuesFormat.InList);
        inList.AddRange(values);
        var lines = new ColumnValues(new ExportColumn("KUERZEL", 0, column), ColumnValuesFormat.Lines);
        lines.AddRange(values);

        Assert.Equal("041; 054; 067", PastedList.ToFilterValue(inList.Finish().Text));
        Assert.Equal("041; 054; 067", PastedList.ToFilterValue(lines.Finish().Text));
    }
}
