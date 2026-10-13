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

    private static string Text(NoteBlock block) => string.Concat(block.Spans.Select(s => s.Text));
}
