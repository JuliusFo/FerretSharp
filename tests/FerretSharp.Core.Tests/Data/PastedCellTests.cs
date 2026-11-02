using FerretSharp.Core.Data;

namespace FerretSharp.Core.Tests.Data;

/// <summary>Ctrl+V on a cell that is not being edited: the one value of the pasted text.</summary>
public class PastedCellTests
{
    [Theory]
    [InlineData("Müller", "Müller")]
    [InlineData("1234,5", "1234,5")]
    [InlineData("", "")] // an empty cell copied: NULL, like an emptied editor
    [InlineData("  K1 ", "  K1 ")] // spaces belong to the value
    [InlineData("Müller\r\n", "Müller")] // one cell from Excel
    [InlineData("Müller\n", "Müller")]
    [InlineData("\r\n", "")]
    public void One_value_is_taken_as_it_is(string pasted, string expected) =>
        Assert.Equal(expected, PastedCell.ValueOf(pasted));

    [Theory]
    [InlineData("\"Zeile 1\nZeile 2\"\r\n", "Zeile 1\nZeile 2")] // Excel quotes a value with a line break
    [InlineData("\"a\tb\"", "a\tb")]
    [InlineData("\"sagt \"\"hallo\"\"\"", "sagt \"hallo\"")]
    public void A_value_quoted_by_Excel_loses_its_quotes(string pasted, string expected) =>
        Assert.Equal(expected, PastedCell.ValueOf(pasted));

    [Theory]
    [InlineData("\"abc\"")] // nothing inside needed the quotes: they are part of the value
    [InlineData("\"")]
    [InlineData("\"a\"b\"")]
    public void Quotes_Excel_did_not_add_stay(string pasted) =>
        Assert.Equal(pasted, PastedCell.ValueOf(pasted));

    [Theory]
    [InlineData("A\tB")] // a row of cells
    [InlineData("A\r\nB\r\n")] // a column of cells
    [InlineData("Zeile 1\r\nZeile 2")] // a text over several lines, not quoted
    [InlineData("\"x\ny\"\t\"z\"")]
    [InlineData("A\r\n\r\n")]
    public void Several_cells_or_lines_are_no_value(string pasted) =>
        Assert.Null(PastedCell.ValueOf(pasted));
}
