using FerretSharp.Core.ClrModel;

namespace FerretSharp.Core.Tests.ClrModel;

/// <summary>The preview's diff (WP-41): changed lines with three lines of context, close changes in one hunk.</summary>
public sealed class LineDiffTests
{
    private static string Lines(int from, int to) => string.Join("\n", Enumerable.Range(from, to - from + 1).Select(i => $"line {i}"));

    private static string Shown(IReadOnlyList<DiffHunk> hunks) => string.Join(" | ", hunks.Select(h => string.Concat(h.Lines.Select(l => l.Kind switch
    {
        DiffLineKind.Added => $"+{l.NewNumber}",
        DiffLineKind.Removed => $"-{l.OldNumber}",
        _ => $" {l.OldNumber}",
    }))));

    [Theory]
    // one line changed: three lines of context around it
    [InlineData(" 7 8 9-10+10 11 12 13", 10, null, null)]
    // two changes 4 lines apart: one hunk
    [InlineData(" 2 3 4-5+5 6 7 8 9-10+10 11 12 13", 5, 10, null)]
    // two changes far apart: two hunks
    [InlineData(" 2 3 4-5+5 6 7 8 |  15 16 17-18+18 19 20", 5, 18, null)]
    // a line added at the end, a line removed at the start
    [InlineData("-1 2 3 4 |  18 19 20+20", null, null, 1)]
    public void Changed_lines_come_with_their_context(string expected, int? changeA, int? changeB, int? moveFirstToEnd)
    {
        var before = Lines(1, 20);
        var lines = before.Split('\n').ToList();
        foreach (var at in new[] { changeA, changeB }.OfType<int>())
        {
            lines[at - 1] = $"changed {at}";
        }

        if (moveFirstToEnd is not null)
        {
            lines.RemoveAt(0);
            lines.Add("new last");
        }

        Assert.Equal(expected, Shown(LineDiff.Of(before, string.Join("\r\n", lines))));
    }

    [Fact]
    public void Line_endings_alone_are_no_change_and_a_new_file_is_one_hunk_of_added_lines()
    {
        Assert.Empty(LineDiff.Of("a\nb\n", "a\r\nb\r\n"));
        Assert.Equal("+1+2", Shown(LineDiff.Of(null, "a\nb\n")));
    }
}
