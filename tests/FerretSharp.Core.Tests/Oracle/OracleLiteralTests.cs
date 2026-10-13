using FerretSharp.Core.Oracle;

namespace FerretSharp.Core.Tests.Oracle;

/// <summary>R5b (review 3.31.0, B12): one way to write text as SQL literal (DDL comments, INSERT export, bind values shown, audit names).</summary>
public sealed class OracleLiteralTests
{
    [Theory]
    [InlineData("O'Brien", false, "'O''Brien'")]
    [InlineData("Łódź", true, "N'Łódź'")]
    [InlineData(null, false, "''")]
    public void Text(string? text, bool national, string literal) => Assert.Equal(literal, OracleLiteral.Text(text, national));

    [Fact]
    public void Escape_doubles_quotes_only() => Assert.Equal("it''s ''x''", OracleLiteral.Escape("it's 'x'"));
}
