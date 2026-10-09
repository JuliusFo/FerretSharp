using System.Globalization;
using FerretSharp.Core.Settings;

namespace FerretSharp.Core.Tests.Settings;

public sealed class UiLanguageTests
{
    [Theory]
    [InlineData("--lang=de", UiLanguage.German)]
    [InlineData("--lang=DE", UiLanguage.German)]
    [InlineData("--lang=en", UiLanguage.English)]
    public void The_command_line_picks_the_language(string argument, UiLanguage expected) =>
        Assert.Equal(expected, UiLanguages.FromArguments(["--data-dir=x", argument]));

    [Theory]
    [InlineData]
    [InlineData("--lang=fr")]
    [InlineData("--lang=")]
    public void Without_a_known_language_the_command_line_picks_none(params string[] args) =>
        Assert.Null(UiLanguages.FromArguments(args));

    [Theory]
    [InlineData("de-DE", UiLanguage.German)]
    [InlineData("de-AT", UiLanguage.German)]
    [InlineData("de", UiLanguage.German)]
    [InlineData("en-US", UiLanguage.English)]
    [InlineData("fr-FR", UiLanguage.English)]
    public void Any_German_culture_shows_German_and_everything_else_English(string culture, UiLanguage expected) =>
        Assert.Equal(expected, UiLanguages.Of(CultureInfo.GetCultureInfo(culture)));

    [Fact]
    public void A_language_maps_to_a_neutral_culture()
    {
        Assert.Equal("de", UiLanguages.Culture(UiLanguage.German).Name);
        Assert.Equal("en", UiLanguages.Culture(UiLanguage.English).Name);
    }
}
