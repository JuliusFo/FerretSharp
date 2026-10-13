using FerretSharp.UI.State;

namespace FerretSharp.UI.Tests.State;

/// <summary>WP-34: the search of the settings page.</summary>
public sealed class SettingsSearchTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Without_a_query_everything_matches(string? query)
    {
        var search = new SettingsSearch(query);

        Assert.False(search.IsActive);
        Assert.True(search.Matches("Sprache"));
    }

    [Theory]
    [InlineData("sperr")]
    [InlineData("WARTEN SPERR")]
    [InlineData("verbindung warten")]
    public void Every_word_must_be_in_label_hint_or_section(string query) =>
        Assert.True(new SettingsSearch(query).Matches("Warten auf gesperrte Zeilen", "Wie lange gewartet wird.", "Verbindung"));

    [Fact]
    public void A_word_that_is_nowhere_does_not_match() =>
        Assert.False(new SettingsSearch("sperr theme").Matches("Warten auf gesperrte Zeilen", null, "Verbindung"));

    [Fact]
    public void Accents_are_ignored_and_placeholders_are_not_text()
    {
        Assert.True(new SettingsSearch("uber").Matches("Über FerretSharp"));
        Assert.False(new SettingsSearch("0").Matches("Zeigt {0} neben dem Wert"));
    }
}
