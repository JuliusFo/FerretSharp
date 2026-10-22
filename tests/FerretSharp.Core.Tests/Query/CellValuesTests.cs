using FerretSharp.Core.Data;
using FerretSharp.Core.Query;
using FerretSharp.Core.Schema;

namespace FerretSharp.Core.Tests.Query;

public class CellValuesTests
{
    private static ColumnInfo Col(string name, string type, int? precision = null, int? scale = null) =>
        new(name, type, null, false, precision, scale, true, false, null, 0);

    private static readonly ColumnInfo Name = Col("NAME", "VARCHAR2");
    private static readonly ColumnInfo Amount = Col("BETRAG", "NUMBER", 10, 2);
    private static readonly ColumnInfo Created = Col("ERSTELLT_AM", "DATE");

    private static CellValuesText Copy(IReadOnlyList<ExportColumn> columns, bool header, params object?[][] rows)
    {
        var copy = new CellValues(columns, header);
        Assert.True(copy.AddRows(rows));
        return copy.Finish();
    }

    [Fact]
    public void Rows_are_lines_and_cells_are_separated_by_tabs_in_the_notation_of_ctrl_c()
    {
        IReadOnlyList<ExportColumn> columns = [new("NAME", 0, Name), new("BETRAG", 1, Amount), new("ERSTELLT_AM", 2, Created)];

        var text = Copy(columns, header: false,
            ["Ferret", 1234.5m, new DateTime(2026, 10, 1, 14, 30, 0)],
            [null, 0.25m, null]);

        Assert.Equal("Ferret\t1234,5\t01.10.2026 14:30:00\r\n\t0,25\t", text.Text);
        Assert.Equal(2, text.Rows);
        Assert.Empty(text.Warnings);
    }

    [Fact]
    public void Columns_pick_their_values_by_source_in_the_order_given()
    {
        IReadOnlyList<ExportColumn> columns = [new("BETRAG", 1, Amount), new("NAME", 0, Name)];

        Assert.Equal("7\tA", Copy(columns, header: false, ["A", 7m]).Text);
    }

    [Fact]
    public void The_header_line_holds_the_headers_as_presented()
    {
        IReadOnlyList<ExportColumn> columns = [new("Name", 0, Name), new("Betrag", 1, Amount)];

        var text = Copy(columns, header: true, ["A", 1m], ["B", 2m]);

        Assert.Equal("Name\tBetrag\r\nA\t1\r\nB\t2", text.Text);
        Assert.Equal(2, text.Rows);
    }

    [Fact]
    public void Cells_show_what_the_presentation_shows_and_are_quoted_like_excel_reads_them()
    {
        IReadOnlyList<ExportColumn> columns = [new("Art", 1, Amount) { Present = value => value is 2m ? "Gewerbe (2)" : value }, new("NAME", 0, Name)];

        Assert.Equal("Gewerbe (2)\t\"zwei\nZeilen\"\r\n7\t\"a\tb\"", Copy(columns, header: false, ["zwei\nZeilen", 2m], ["a\tb", 7m]).Text);
    }

    [Fact]
    public void One_column_gives_the_text_of_the_column_values()
    {
        var column = new ExportColumn("NAME", 0, Name);
        object?[] values = ["B", null, "zwei\nZeilen", "sagt \"hallo\""];
        var lines = new ColumnValues(column, ColumnValuesFormat.Lines);
        lines.AddRange(values);

        var cells = Copy([column], header: false, [.. values.Select(v => new[] { v })]);

        Assert.Equal(lines.Finish().Text, cells.Text);
    }

    [Fact]
    public void More_cells_than_the_limit_copy_nothing()
    {
        Assert.True(CellValues.Fits(ColumnValues.MaxValues / 4, 4));
        Assert.False(CellValues.Fits(ColumnValues.MaxValues / 4 + 1, 4));
        var copy = new CellValues([new("NAME", 0, Name), new("BETRAG", 1, Amount)], header: true);

        for (var i = 0; i < ColumnValues.MaxValues / 2; i++)
        {
            Assert.True(copy.AddRow(["A", 1m]));
        }

        Assert.False(copy.AddRow(["A", 1m]));
        Assert.False(copy.AddRow(["A", 1m]));
        Assert.True(copy.TooMany);
        Assert.False(copy.TooLong);
        Assert.Throws<InvalidOperationException>(() => copy.Finish());
    }

    [Fact]
    public void Too_many_characters_copy_nothing_either()
    {
        var copy = new CellValues([new("NAME", 0, Name)], header: false);
        var text = new string('x', 1_000_000);

        var added = 0;
        while (copy.AddRow([text]))
        {
            added++;
        }

        Assert.Equal(ColumnValues.MaxCharacters / text.Length, added);
        Assert.True(copy.TooMany);
        Assert.True(copy.TooLong);
    }

    [Fact]
    public void A_lob_with_only_its_preview_loaded_is_named()
    {
        var clob = Col("TEXT", "CLOB");

        var text = Copy([new("TEXT", 0, clob)], header: false, [new LobValue("Anfang", 5000)]);

        Assert.Equal("", text.Text);
        Assert.NotEmpty(text.Warnings);
    }
}
