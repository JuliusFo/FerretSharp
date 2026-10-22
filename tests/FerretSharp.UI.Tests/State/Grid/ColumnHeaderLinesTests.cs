using FerretSharp.Core.Schema;
using FerretSharp.Core.Settings;
using FerretSharp.UI.State;

namespace FerretSharp.UI.Tests.State;

/// <summary>Type and NOT NULL on the last line of a column header, each switchable; without both the line goes.</summary>
public sealed class ColumnHeaderLinesTests
{
    private static readonly ColumnInfo Id = new("KUNDE_ID", "NUMBER", null, false, 10, 0, false, false, null, 1);

    private static readonly ColumnInfo Note = new("NOTIZ", "VARCHAR2", 200, false, null, null, true, false, null, 2);

    [Theory]
    [InlineData(true, true, "NUMBER(10) · NOT NULL", "VARCHAR2(200)")]
    [InlineData(true, false, "NUMBER(10)", "VARCHAR2(200)")]
    [InlineData(false, true, "NOT NULL", "")]
    [InlineData(false, false, "", "")]
    public void The_line_shows_what_is_switched_on(bool types, bool notNull, string id, string note)
    {
        var header = new ColumnHeaderLines(types, notNull);

        Assert.Equal((id, note), (header.TextOf(Id), header.TextOf(Note)));
        Assert.Equal(types || notNull, header.TypeLine);
    }

    [Fact]
    public void What_the_line_hides_goes_into_the_tooltip()
    {
        Assert.Null(ColumnHeaderLines.All.HintOf(Id));
        Assert.Equal("NUMBER(10) · NOT NULL", new ColumnHeaderLines(false, true).HintOf(Id));
        Assert.Equal("NUMBER(10) · NOT NULL", new ColumnHeaderLines(true, false).HintOf(Id));
        Assert.Null(new ColumnHeaderLines(true, false).HintOf(Note)); // nullable: the line shows all there is
        Assert.Equal("VARCHAR2(200)", new ColumnHeaderLines(false, false).HintOf(Note));
    }

    [Fact]
    public void The_header_is_lower_by_a_line_without_type_line()
    {
        var hidden = new ColumnHeaderLines(false, false);
        var notNull = new ColumnHeaderLines(false, true);

        Assert.Equal(GridOptions.ClrHeaderHeight, ColumnHeaderLines.All.HeightWith(clrLine: true));
        Assert.Null(ColumnHeaderLines.All.HeightWith(clrLine: false));
        Assert.Null(notNull.HeightWith(clrLine: false));
        Assert.Null(hidden.HeightWith(clrLine: true));
        Assert.Equal(ColumnHeaderLines.NameOnlyHeight, hidden.HeightWith(clrLine: false));
    }

    [Fact]
    public void Follows_the_settings_and_shows_everything_by_default()
    {
        Assert.Equal(ColumnHeaderLines.All, ColumnHeaderLines.Of(AppSettings.Default));
        Assert.Equal(new ColumnHeaderLines(false, true), ColumnHeaderLines.Of(AppSettings.Default with { ShowColumnTypes = false }));
    }
}
