using System.Globalization;
using FerretSharp.Core.Connections;
using FerretSharp.Core.Resources;

namespace FerretSharp.Core.Tests;

/// <summary>The localized texts of the Core (WP-29, ADR 0017).</summary>
public sealed class ResourceTests
{
    public static TheoryData<string> TextClasses() => [.. ResourceChecks.TextClasses(typeof(TextFormat).Assembly).Select(t => t.Name)];

    [Theory]
    [MemberData(nameof(TextClasses))]
    public void Every_text_exists_in_English_and_German_with_the_same_placeholders(string textClass)
    {
        var type = ResourceChecks.TextClasses(typeof(TextFormat).Assembly).Single(t => t.Name == textClass);

        Assert.Empty(ResourceChecks.Problems(type));
    }

    [Fact]
    public void The_texts_follow_the_UI_culture()
    {
        var profile = new ConnectionProfile(Guid.NewGuid(), "", ConnectionKind.Dev, new TnsAliasAddress("ORCL", null), "scott", null, false);

        using (UiCulture.Use("en-US"))
        {
            Assert.Equal("Name is missing.", ConnectionProfileValidator.Validate(profile)[ConnectionField.Name]);
        }

        Assert.Equal("Name fehlt.", ConnectionProfileValidator.Validate(profile)[ConnectionField.Name]);
    }

    [Fact]
    public void Plural_picks_the_form_by_count_and_fills_the_placeholders()
    {
        Assert.Equal("1 row in T", TextFormat.Plural(1, "{0} row in {1}", "{0} rows in {1}", "T"));
        Assert.Equal("0 rows in T", TextFormat.Plural(0, "{0} row in {1}", "{0} rows in {1}", "T"));
        Assert.Equal("1.234 Zeilen", TextFormat.Plural(CultureInfo.GetCultureInfo("de-DE"), 1234, "{0:N0} Zeile", "{0:N0} Zeilen"));
    }
}
