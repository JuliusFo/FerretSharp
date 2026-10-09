using FerretSharp.UI.Components;
using FerretSharp.UI.Resources;

namespace FerretSharp.UI.Tests;

/// <summary>The localized texts of the UI (WP-29, ADR 0017).</summary>
public sealed class ResourceTests
{
    public static TheoryData<string> TextClasses() => [.. ResourceChecks.TextClasses(typeof(CommonText).Assembly).Select(t => t.Name)];

    [Theory]
    [MemberData(nameof(TextClasses))]
    public void Every_text_exists_in_English_and_German_with_the_same_placeholders(string textClass)
    {
        var type = ResourceChecks.TextClasses(typeof(CommonText).Assembly).Single(t => t.Name == textClass);

        Assert.Empty(ResourceChecks.Problems(type));
    }

    [Fact]
    public void The_texts_follow_the_UI_culture()
    {
        using (UiCulture.Use("en"))
        {
            Assert.Equal("Cancel", CommonText.Cancel);
        }

        Assert.Equal("Abbrechen", CommonText.Cancel);
    }

    [Fact]
    public void Fmt_splits_a_text_into_literals_and_placeholders_in_the_order_of_the_text()
    {
        Assert.Equal(
            [("Wait ", null), (null, 1), (" for ", null), (null, 0), (".", null)],
            Fmt.Split("Wait {1} for {0}."));
        Assert.Equal([("{literal} ", null), (null, 0)], Fmt.Split("{{literal}} {0}"));
        Assert.Equal([("no placeholder", null)], Fmt.Split("no placeholder"));
    }
}
