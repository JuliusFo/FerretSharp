using FerretSharp.UI.State;

namespace FerretSharp.UI.Tests.State;

/// <summary>WP-32: the release notes of an update are a changelog section, shown as plain blocks (no HTML from the internet).</summary>
public sealed class ReleaseNotesTests
{
    [Fact]
    public void A_changelog_section_becomes_headings_bullets_and_paragraphs()
    {
        var blocks = ReleaseNotes.Parse("""
            Installer and update notice (WP-32).

            ### Added
            - Install once with `Setup.exe`;
              FerretSharp reports new versions.
            - **Check now** in [Settings](https://example.com/settings).

            ---
            """);

        Assert.Collection(
            blocks,
            b => Assert.Equal((NoteBlockKind.Paragraph, "Installer and update notice (WP-32)."), (b.Kind, Text(b))),
            b => Assert.Equal((NoteBlockKind.Heading, "Added"), (b.Kind, Text(b))),
            b =>
            {
                Assert.Equal((NoteBlockKind.Bullet, "Install once with Setup.exe; FerretSharp reports new versions."), (b.Kind, Text(b)));
                Assert.Equal(new NoteSpan("Setup.exe", Code: true), b.Spans[1]);
            },
            b => Assert.Equal((NoteBlockKind.Bullet, "Check now in Settings."), (b.Kind, Text(b))));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  \n ")]
    public void No_notes_are_no_blocks(string? notes) => Assert.Empty(ReleaseNotes.Parse(notes));

    [Fact]
    public void Unclosed_markers_stay_text()
    {
        var block = Assert.Single(ReleaseNotes.Parse("A ` and [a link without target"));

        Assert.Equal("A ` and [a link without target", Text(block));
        Assert.All(block.Spans, s => Assert.False(s.Code));
    }

    /// <summary>
    /// Review 3.40.0, C7: the dialog shows as an image exactly the lines the release workflow rewrote – a line of its own;
    /// one in a sentence stays text in both.
    /// </summary>
    [Theory]
    [InlineData("![The dialog]({0}3.34.0-dialog.png)")]
    [InlineData("  ![The dialog]({0}3.34.0-dialog.png)")]
    [InlineData("- See ![the dialog]({0}3.34.0-dialog.png) here.")]
    [InlineData("![Two]({0}a.png) ![images]({0}b.png)")]
    public void The_dialog_shows_as_image_what_the_release_workflow_takes_for_one(string template)
    {
        var line = string.Format(System.Globalization.CultureInfo.InvariantCulture, template, Own);

        var image = ReleaseNotes.Parse(line).Any(b => b.Kind == NoteBlockKind.Image);

        Assert.Equal(ReleaseWorkflow.ImageLine.IsMatch(line), image);
    }

    private const string Own = "https://raw.githubusercontent.com/JuliusFo/FerretSharp/v3.34.0/docs/images/changelog/";

    [Fact]
    public void An_image_line_of_the_project_becomes_an_image_block()
    {
        var blocks = ReleaseNotes.Parse($$"""
            ### Added
            - The dialog shows images.
              ![The update dialog with an image]({{Own}}3.34.0-update-dialog.png)
            - A second point.
            """);

        Assert.Collection(
            blocks,
            b => Assert.Equal(NoteBlockKind.Heading, b.Kind),
            b => Assert.Equal((NoteBlockKind.Bullet, "The dialog shows images."), (b.Kind, Text(b))),
            b =>
            {
                Assert.Equal((NoteBlockKind.Image, "The update dialog with an image"), (b.Kind, Text(b)));
                Assert.Equal(new Uri(Own + "3.34.0-update-dialog.png"), b.Image);
            },
            b => Assert.Equal((NoteBlockKind.Bullet, "A second point."), (b.Kind, Text(b))));
    }

    [Theory]
    [InlineData("http://raw.githubusercontent.com/JuliusFo/FerretSharp/v3.34.0/a.png")]
    [InlineData("https://example.com/JuliusFo/FerretSharp/v3.34.0/a.png")]
    [InlineData("https://raw.githubusercontent.com.example.com/JuliusFo/FerretSharp/v3.34.0/a.png")]
    [InlineData("https://raw.githubusercontent.com@example.com/JuliusFo/FerretSharp/v3.34.0/a.png")]
    [InlineData("https://raw.githubusercontent.com:8443/JuliusFo/FerretSharp/v3.34.0/a.png")]
    [InlineData("https://raw.githubusercontent.com/Someone/FerretSharp/v3.34.0/a.png")]
    [InlineData("https://raw.githubusercontent.com/JuliusFo/FerretSharpX/v3.34.0/a.png")]
    [InlineData("https://raw.githubusercontent.com/JuliusFo/FerretSharp/../Other/v1/a.png")]
    [InlineData("https://raw.githubusercontent.com/JuliusFo/FerretSharp/%2e%2e/Other/a.png")]
    [InlineData("https://raw.githubusercontent.com/JuliusFo/FerretSharp/v3.34.0/a.png?x=1")]
    [InlineData("https://raw.githubusercontent.com/JuliusFo/FerretSharp/a.png")]
    [InlineData("docs/images/changelog/a.png")]
    public void An_image_from_elsewhere_shows_its_alt_text(string url)
    {
        var block = Assert.Single(ReleaseNotes.Parse($"![The alt text]({url})"));

        Assert.Equal((NoteBlockKind.Paragraph, "The alt text", null), (block.Kind, Text(block), block.Image));
    }

    [Fact]
    public void An_image_from_elsewhere_without_alt_text_is_left_out() =>
        Assert.Empty(ReleaseNotes.Parse("![](https://example.com/a.png)"));

    [Fact]
    public void An_image_in_the_middle_of_text_stays_text()
    {
        var block = Assert.Single(ReleaseNotes.Parse($"See ![the dialog]({Own}a.png) here."));

        Assert.Equal((NoteBlockKind.Paragraph, "See !the dialog here."), (block.Kind, Text(block)));
    }

    private static string Text(NoteBlock block) => string.Concat(block.Spans.Select(s => s.Text));
}
