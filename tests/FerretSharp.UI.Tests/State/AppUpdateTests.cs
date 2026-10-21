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
    public void The_target_keeps_its_spelling_when_its_notes_come_from_between()
    {
        var update = AppUpdate.From(new VersionNotes("3.33", null), [new("3.33.0", "- from the delta"), new("3.32.0", "- older")]);

        Assert.Equal("3.33", update.Version);
        Assert.Equal([new VersionNotes("3.33", "- from the delta"), new VersionNotes("3.32.0", "- older")], update.Notes);
    }

    [Fact]
    public void Text_that_is_no_version_sorts_after_all_versions_without_breaking_the_sort()
    {
        // 9.0.0 < 10.0.0 by number, "10.0.0" < "5x" < "9.0.0" as text: compared as text across the groups, the order had a
        // cycle, and List.Sort may throw on one beyond 16 entries.
        var between = Enumerable.Range(1, 30)
            .SelectMany(i => new VersionNotes[] { new($"{i}.0.0", "- v"), new($"{i}x", "- t") })
            .ToList();

        var update = AppUpdate.From(new VersionNotes("31.0.0", "- target"), between);

        Assert.Equal(61, update.Notes.Count);
        Assert.Equal(
            ["31.0.0", "30.0.0", "29.0.0"],
            update.Notes.Take(3).Select(n => n.Version));
        Assert.Equal(
            Enumerable.Range(1, 30).Select(i => $"{i}x").OrderByDescending(v => v, StringComparer.Ordinal),
            update.Notes.Skip(31).Select(n => n.Version));
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
    [InlineData("3.30.0", "next", 1)]
    [InlineData("abc", "abd", -1)]
    public void Release_versions_compare_by_number(string a, string b, int expected)
    {
        Assert.Equal(expected, Math.Sign(ReleaseVersion.Compare(a, b)));
    }
}
