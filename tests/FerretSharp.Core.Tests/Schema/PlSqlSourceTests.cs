using FerretSharp.Core.Query;
using FerretSharp.Core.Schema;
using NSubstitute;

namespace FerretSharp.Core.Tests.Schema;

public class PlSqlSourceTests
{
    [Theory]
    [InlineData("  x := 1;\n", "  x := 1;")]
    [InlineData("  x := 1;\r\n", "  x := 1;")]
    [InlineData("  x := 1;\r", "  x := 1;")]
    [InlineData("END;", "END;")]
    [InlineData("\n", "")]
    [InlineData(null, "")]
    public void A_line_loses_only_its_line_break(string? text, string expected) => Assert.Equal(expected, PlSqlSourceText.LineOf(text));

    [Fact]
    public void Text_has_one_line_per_dictionary_line()
    {
        var source = new PlSqlSource(["PROCEDURE P IS", "BEGIN", "  NULL;", "END;"]);

        Assert.Equal("PROCEDURE P IS\nBEGIN\n  NULL;\nEND;", source.Text);
        Assert.False(source.IsWrapped);
        Assert.False(source.IsEmpty);
    }

    [Fact]
    public void Wrapped_source_is_recognized_from_its_header()
    {
        // Wrapped units hold many text lines per dictionary line.
        var source = new PlSqlSource(["PACKAGE BODY pkg_geheim wrapped \na000000\n369\nabcd\nabcd\n", "b\n2d 6d\nyQ3"]);

        Assert.True(source.IsWrapped);
    }

    [Fact]
    public void Wrapped_needs_the_second_header_line()
    {
        // A procedure that happens to be called WRAPPED.
        Assert.False(new PlSqlSource(["PROCEDURE wrapped", "IS BEGIN NULL; END;"]).IsWrapped);
        Assert.True(new PlSqlSource(["PROCEDURE \"App\".\"P\" wrapped", "a000000", "1"]).IsWrapped);
        Assert.False(new PlSqlSource([]).IsWrapped);
        Assert.True(new PlSqlSource([]).IsEmpty);
    }
}
