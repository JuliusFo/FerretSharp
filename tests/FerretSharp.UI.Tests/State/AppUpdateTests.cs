using FerretSharp.UI.State;

namespace FerretSharp.UI.Tests.State;

/// <summary>An update over several versions shows the notes of each, newest first (from 3.29.0 to 3.33.0: 3.33.0 to 3.30.0).</summary>
public sealed class AppUpdateTests
{
    [Fact]
    public void The_versions_between_come_newest_first_by_number_not_by_text()
    {
        var update = AppUpdate.From(
            new VersionNotes("3.10.0", "- ten"),
            [new("3.9.0", "- nine"), new("3.10.0", "- ten"), new("3.8.1", "- eight")]);

        Assert.Equal("3.10.0", update.Version);
        Assert.True(update.NotesComplete);
        Assert.Equal(
            [new VersionNotes("3.10.0", "- ten"), new VersionNotes("3.9.0", "- nine"), new VersionNotes("3.8.1", "- eight")],
            update.Notes);
    }

    [Fact]
    public void Without_the_versions_between_only_the_target_is_known()
    {
        var update = AppUpdate.From(new VersionNotes("3.33.0", "### Fixed\n- a fix"));

        Assert.False(update.NotesComplete);
        Assert.Equal([new VersionNotes("3.33.0", "### Fixed\n- a fix")], update.Notes);
        Assert.False(AppUpdate.From(new VersionNotes("3.33.0", null), []).NotesComplete);
    }

    [Fact]
    public void Each_version_comes_once_and_blank_notes_are_none()
    {
        var update = AppUpdate.From(
            new VersionNotes("3.33.0", "  \n"),
            [new("3.33.0", "- from the delta"), new("3.32.0", " "), new("3.32.0", "- second")]);

        Assert.Equal([new VersionNotes("3.33.0", "- from the delta"), new VersionNotes("3.32.0", "- second")], update.Notes);
    }

    [Fact]
    public void A_version_newer_than_the_target_is_left_out()
    {
        var update = AppUpdate.From(new VersionNotes("3.31.0", "- target"), [new("3.32.0", "- newer"), new("3.31.0", "- target")]);

        Assert.Equal([new VersionNotes("3.31.0", "- target")], update.Notes);
    }

    [Theory]
    [InlineData("3.10.0", "3.9.0", 1)]
    [InlineData("3.9.0", "3.10.0", -1)]
    [InlineData("3.30", "3.30.0", 0)]
    [InlineData("3.30.0+build.5", "3.30.0", 0)]
    [InlineData("3.30.0-beta.1", "3.30.0", -1)]
    [InlineData("3.30.0-beta.2", "3.30.0-beta.1", 1)]
    [InlineData("4.0.0-rc", "3.99.0", 1)]
    public void Release_versions_compare_by_number(string a, string b, int expected)
    {
        Assert.Equal(expected, Math.Sign(ReleaseVersion.Compare(a, b)));
    }
}
