using System.Text.RegularExpressions;

namespace FerretSharp.Core.Tests;

/// <summary>
/// WP-49: images in CHANGELOG.md. The release workflow rewrites their relative paths to the tag's files on GitHub, and the
/// update dialog shows only a line of its own from there (<c>ReleaseNotes.IsOwnImage</c>) – so each image is a line of its
/// own, relative, directly below <c>docs/images/changelog/</c> with a plain file name (as the workflow checks), and
/// exists. Runs in the release workflow too (Core tests), before anything is packed.
/// </summary>
public sealed partial class ChangelogTests
{
    private const string Folder = "docs/images/changelog/";
    private const long MaxBytes = 400 * 1024;

    [Fact]
    public void Every_image_is_a_line_of_its_own_and_exists_in_the_repository() =>
        Assert.Empty(Problems(File.ReadAllLines(Path.Combine(Repository.Root, "CHANGELOG.md")), Repository.Root));

    [Fact]
    public void The_check_finds_every_kind_of_wrong_image()
    {
        using var folder = new TestFolder();
        Directory.CreateDirectory(folder.Combine("docs", "images", "changelog"));
        File.WriteAllBytes(folder.Combine("docs", "images", "changelog", "3.34.0-dialog.png"), new byte[10]);
        File.WriteAllBytes(folder.Combine("docs", "images", "changelog", "3.34.0-big.png"), new byte[MaxBytes + 1]);

        var problems = Problems(
            [
                "- A point.",
                "  ![The dialog](docs/images/changelog/3.34.0-dialog.png)",
                "See ![the dialog](docs/images/changelog/3.34.0-dialog.png) here.",
                "![](docs/images/changelog/3.34.0-dialog.png)",
                "![Elsewhere](https://example.com/a.png)",
                "![Other folder](docs/images/grid-dark.png)",
                "![Up](docs/images/changelog/../grid-dark.png)",
                "![Space](docs/images/changelog/my%20dialog.png)",
                "![Missing](docs/images/changelog/3.34.0-missing.png)",
                "![Big](docs/images/changelog/3.34.0-big.png)",
            ],
            folder.Path);

        Assert.Collection(
            problems,
            p => Assert.StartsWith("CHANGELOG.md:3: an image needs a line of its own", p, StringComparison.Ordinal),
            p => Assert.StartsWith("CHANGELOG.md:4: no alt text", p, StringComparison.Ordinal),
            p => Assert.StartsWith("CHANGELOG.md:5: https://example.com/a.png is not", p, StringComparison.Ordinal),
            p => Assert.StartsWith("CHANGELOG.md:6: docs/images/grid-dark.png is not", p, StringComparison.Ordinal),
            p => Assert.StartsWith("CHANGELOG.md:7: docs/images/changelog/../grid-dark.png is not", p, StringComparison.Ordinal),
            p => Assert.StartsWith("CHANGELOG.md:8: docs/images/changelog/my%20dialog.png is not", p, StringComparison.Ordinal),
            p => Assert.StartsWith("CHANGELOG.md:9: docs/images/changelog/3.34.0-missing.png does not exist", p, StringComparison.Ordinal),
            p => Assert.StartsWith("CHANGELOG.md:10: docs/images/changelog/3.34.0-big.png has 400 KB", p, StringComparison.Ordinal));
    }

    private static List<string> Problems(IReadOnlyList<string> lines, string root)
    {
        var problems = new List<string>();
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i].Trim();
            if (!line.Contains("![", StringComparison.Ordinal))
            {
                continue;
            }

            var at = $"CHANGELOG.md:{i + 1}";
            if (ImageLine().Match(line) is not { Success: true } image)
            {
                problems.Add($"{at}: an image needs a line of its own, ![alt](path) – in a sentence the dialog shows \"!alt\".");
                continue;
            }

            var path = image.Groups["path"].Value;
            if (image.Groups["alt"].Value.Trim().Length == 0)
            {
                problems.Add($"{at}: no alt text (shown offline and in older versions).");
            }

            if (!path.StartsWith(Folder, StringComparison.Ordinal)
                || !FileName().IsMatch(path[Folder.Length..]) || path[Folder.Length..] is "." or "..")
            {
                problems.Add($"{at}: {path} is not a file directly below {Folder} (letters, digits, '.', '-', '_').");
                continue;
            }

            var file = new FileInfo(Path.Combine(root, path));
            if (!file.Exists)
            {
                problems.Add($"{at}: {path} does not exist.");
            }
            else if (file.Length > MaxBytes)
            {
                problems.Add($"{at}: {path} has {file.Length / 1024} KB – crop it (docs/release.md › Bilder im Changelog).");
            }
        }

        return problems;
    }

    [GeneratedRegex(@"^!\[(?<alt>[^\]]*)\]\((?<path>[^)\s]+)\)$")]
    private static partial Regex ImageLine();

    [GeneratedRegex(@"^[A-Za-z0-9._-]+$")]
    private static partial Regex FileName();
}
