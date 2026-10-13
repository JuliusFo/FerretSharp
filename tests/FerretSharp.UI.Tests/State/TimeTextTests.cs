using System.Globalization;
using FerretSharp.UI.State;

namespace FerretSharp.UI.Tests.State;

/// <summary>R5b (review 3.31.0, B12): one way to write a duration in the footers, the LINQ console and the model page.</summary>
public sealed class TimeTextTests
{
    [Theory]
    [InlineData(12, "12 ms")]
    [InlineData(999, "999 ms")]
    [InlineData(1_400, "1,4 s")]
    [InlineData(59_940, "59,9 s")]
    [InlineData(125_000, "2 min 5 s")]
    public void Duration(int milliseconds, string text)
    {
        var culture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
        try
        {
            Assert.Equal(text, TimeText.Duration(TimeSpan.FromMilliseconds(milliseconds)));
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }
    }
}
