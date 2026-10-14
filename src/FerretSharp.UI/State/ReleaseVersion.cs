namespace FerretSharp.UI.State;

/// <summary>
/// Orders release versions as SemVer does where it matters for the update dialog: by number (3.10.0 after 3.9.0), a
/// prerelease before its release, build metadata ignored. Text that is no version compares as text.
/// </summary>
public static class ReleaseVersion
{
    public static int Compare(string a, string b)
    {
        var (numberA, prereleaseA) = Split(a);
        var (numberB, prereleaseB) = Split(b);
        if (!Version.TryParse(numberA, out var versionA) || !Version.TryParse(numberB, out var versionB))
        {
            return string.CompareOrdinal(a, b);
        }

        var byNumber = Normalize(versionA).CompareTo(Normalize(versionB));
        if (byNumber != 0)
        {
            return byNumber;
        }

        return (prereleaseA, prereleaseB) switch
        {
            (null, null) => 0,
            (null, _) => 1,
            (_, null) => -1,
            _ => string.CompareOrdinal(prereleaseA, prereleaseB),
        };
    }

    private static (string Number, string? Prerelease) Split(string version)
    {
        var plus = version.IndexOf('+');
        var core = (plus < 0 ? version : version[..plus]).Trim();
        var dash = core.IndexOf('-');
        return dash < 0 ? (core, null) : (core[..dash], core[(dash + 1)..]);
    }

    /// <summary>3.30 and 3.30.0 are one version (<see cref="Version"/> marks a missing part as -1).</summary>
    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0));
}
