namespace FerretSharp.UI.State;

/// <summary>How long the link pipe waits before listening again after failures (review 3.40.0, A27).</summary>
public static class LinkPipeRetry
{
    public static readonly TimeSpan MaxDelay = TimeSpan.FromMinutes(5);

    /// <summary>2 s after the first failure, doubling with each further one, at most <see cref="MaxDelay"/>.</summary>
    public static TimeSpan Delay(int failures)
    {
        var delay = TimeSpan.FromSeconds(2 * Math.Pow(2, Math.Clamp(failures - 1, 0, 10)));
        return delay < MaxDelay ? delay : MaxDelay;
    }
}

/// <summary>Which running Visual Studio gets a file (WP-40; review 3.44.0, C8: out of the app, with tests).</summary>
public static class VisualStudioMatch
{
    /// <summary>
    /// Higher is better: the solution itself beats a solution whose folder holds the file (the deeper, the better); -1 = no.
    /// The folder must hold the file, not only start with the same characters (<c>C:\repo</c> is no folder of <c>C:\repo2</c>).
    /// </summary>
    /// <param name="open">The solution the instance has open.</param>
    /// <param name="solution">The solution the file belongs to, if known.</param>
    public static int Score(string open, string file, string? solution)
    {
        if (solution is not null && string.Equals(Path.GetFullPath(open), Path.GetFullPath(solution), StringComparison.OrdinalIgnoreCase))
        {
            return int.MaxValue;
        }

        var folder = Path.GetDirectoryName(Path.GetFullPath(open));
        return folder is not null && Path.GetFullPath(file).StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            ? folder.Length
            : -1;
    }
}
