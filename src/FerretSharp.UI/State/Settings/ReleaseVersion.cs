namespace FerretSharp.UI.State;

/// <summary>
/// Orders release versions for the update dialog: by number (3.10.0 after 3.9.0), a prerelease before its release, build
/// metadata ignored. Unlike SemVer, prerelease labels compare as text (beta.10 before beta.2) – the app looks for releases
/// only. Text that is no version comes before every version and compares as text, so the order stays total (List.Sort
/// throws on an inconsistent one).
/// </summary>
public static class ReleaseVersion
{
    public static int Compare(string a, string b)
    {
        var (numberA, prereleaseA) = Split(a);
        var (numberB, prereleaseB) = Split(b);
        Version.TryParse(numberA, out var versionA);
        Version.TryParse(numberB, out var versionB);
        if (versionA is null || versionB is null)
        {
            return (versionA, versionB) switch
            {
                (null, null) => string.CompareOrdinal(a, b),
                (null, _) => -1,
                _ => 1,
            };
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
