using System.Text.RegularExpressions;

namespace FerretSharp.Testing;

/// <summary>
/// The image rules of the release workflow (WP-49), read from <c>.github/workflows/release.yml</c> – so the tests of the
/// changelog check and of the update dialog can compare their own rule with the workflow's (review 3.40.0, C7: the rule
/// was written three times and each test checked only its own copy). PowerShell's regular expressions are .NET's.
/// </summary>
internal static partial class ReleaseWorkflow
{
    private static readonly Lazy<string> Text = new(() => File.ReadAllText(Path.Combine(Repository.Root, ".github", "workflows", "release.yml")));

    /// <summary>A line the workflow takes as an image line and rewrites (<c>[regex]::Replace($changes, '…'</c>).</summary>
    public static Regex ImageLine => new(Pattern(ReplacePattern()), RegexOptions.None);

    /// <summary>An image path the workflow accepts before it checks that the file exists (<c>$path -notmatch '…'</c>).</summary>
    public static Regex ImagePath => new(Pattern(PathPattern()), RegexOptions.None);

    private static string Pattern(Regex finder) =>
        finder.Match(Text.Value) is { Success: true } found
            ? found.Groups["pattern"].Value.Replace("''", "'", StringComparison.Ordinal)
            : throw new InvalidOperationException($"release.yml no longer has the pattern {finder}.");

    [GeneratedRegex(@"\[regex\]::Replace\(\$changes, '(?<pattern>(?:[^']|'')*)'")]
    private static partial Regex ReplacePattern();

    [GeneratedRegex(@"\$path -notmatch '(?<pattern>(?:[^']|'')*)'")]
    private static partial Regex PathPattern();
}
