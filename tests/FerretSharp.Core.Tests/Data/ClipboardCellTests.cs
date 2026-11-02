using FerretSharp.Core.Data;
using FerretSharp.Core.Schema;

namespace FerretSharp.Core.Tests.Data;

/// <summary>One cell on the clipboard: the text Ctrl+C gives, the one value Ctrl+V takes from a text.</summary>
public class ClipboardCellTests
{
    private static readonly ColumnInfo Text = new("T", "VARCHAR2", null, false, null, null, true, false, null, 0);

    [Theory]
    [InlineData("Müller", "Müller")]
    [InlineData("1234,5", "1234,5")]
    [InlineData("", "")] // an empty cell copied: NULL, like an emptied editor
    [InlineData("  K1 ", "  K1 ")] // spaces belong to the value
    [InlineData("Müller\r\n", "Müller")] // one cell from Excel
    [InlineData("Müller\n", "Müller")]
    [InlineData("\r\n", "")]
    public void One_value_is_taken_as_it_is(string pasted, string expected) =>
        Assert.Equal(expected, ClipboardCell.ValueOf(pasted));

    [Theory]
    [InlineData("\"Zeile 1\nZeile 2\"\r\n", "Zeile 1\nZeile 2")] // Excel quotes a value with a line break
    [InlineData("\"a\tb\"", "a\tb")]
    [InlineData("\"sagt \"\"hallo\"\"\"", "sagt \"hallo\"")]
    public void A_value_quoted_by_Excel_loses_its_quotes(string pasted, string expected) =>
        Assert.Equal(expected, ClipboardCell.ValueOf(pasted));

    [Theory]
    [InlineData("\"abc\"")] // nothing inside needed the quotes: they are part of the value
    [InlineData("\"")]
    [InlineData("\"a\"b\"")]
    public void Quotes_Excel_did_not_add_stay(string pasted) =>
        Assert.Equal(pasted, ClipboardCell.ValueOf(pasted));

    [Theory]
    [InlineData("A\tB")] // a row of cells
    [InlineData("A\r\nB\r\n")] // a column of cells
    [InlineData("Zeile 1\r\nZeile 2")] // a text over several lines, not quoted
    [InlineData("\"x\ny\"\t\"z\"")]
    [InlineData("A\r\n\r\n")]
    public void Several_cells_or_lines_are_no_value(string pasted) =>
        Assert.Null(ClipboardCell.ValueOf(pasted));

    [Theory]
    [InlineData("Müller", "Müller")]
    [InlineData("sagt \"hallo\"", "sagt \"hallo\"")] // quotes alone need none (as Excel)
    [InlineData("\"abc\"", "\"abc\"")]
    [InlineData("Zeile 1\nZeile 2", "\"Zeile 1\nZeile 2\"")]
    [InlineData("Text\r\n", "\"Text\r\n\"")] // a line break at the end stays
    [InlineData("a\tb", "\"a\tb\"")]
    [InlineData("\"x\ny\"", "\"\"\"x\ny\"\"\"")]
    [InlineData("\"A\"\"B\"", "\"\"\"A\"\"\"\"B\"\"\"")] // would pass for Excel's quotes: quoted itself
    public void Ctrl_C_quotes_a_value_as_Excel_where_pasting_would_change_it(string value, string copied)
    {
        Assert.Equal(copied, ClipboardCell.Copied(Text, value).Text);
        Assert.Equal(value, ClipboardCell.ValueOf(copied)); // pasted back whole
    }

    [Fact]
    public void Ctrl_C_of_null_is_empty_and_pastes_back_as_null() =>
        Assert.Equal("", ClipboardCell.ValueOf(ClipboardCell.Copied(Text, null).Text));
}
