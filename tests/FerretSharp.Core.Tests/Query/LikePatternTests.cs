using FerretSharp.Core.Query;
using FerretSharp.Core.Schema;
using NSubstitute;

namespace FerretSharp.Core.Tests.Query;

public class LikePatternTests
{
    [Theory]
    [InlineData("KUNDEN_ID", "%KUNDEN\\_ID%")]
    [InlineData("100%", "%100\\%%")]
    [InlineData("a\\b", "%a\\\\b%")]
    public void Wildcards_of_the_text_are_escaped(string text, string expected) => Assert.Equal(expected, LikePattern.Contains(text));
}
