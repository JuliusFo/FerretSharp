namespace FerretSharp.Core.ClrModel;

/// <summary>How far a build of the solution ("Build projects") has come.</summary>
/// <param name="Done">Projects of the solution that wrote their output so far, each once.</param>
/// <param name="Total">Projects of the solution that build an output; 0 when the solution could not be read.</param>
/// <param name="Last">The project that finished last; null before the first.</param>
public sealed record SolutionBuildProgress(int Done, int Total, string? Last)
{
    /// <summary>Done against total, at most 1; null without a total.</summary>
    public double? Fraction => Total > 0 ? Math.Min(1.0, (double)Done / Total) : null;
}

/// <summary>
/// Follows the output of <c>dotnet build -v minimal</c>: every project that wrote its output logs a line
/// "<c>Name -> path</c>" (also when it was up to date). Only names of the solution's projects count, and each once – a
/// project with several target frameworks logs one line per framework, and lines of errors and warnings never name a
/// project alone. The lines come in any order (projects build in parallel). Not thread-safe: the output of one process
/// arrives line by line.
/// </summary>
public sealed class SolutionBuildTracker
{
    private const string Arrow = " -> ";

    private readonly HashSet<string> _projects;
    private readonly HashSet<string> _done = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="projectFiles">The solution's project files; shared projects (<c>.shproj</c>) never build and are left out.</param>
    public SolutionBuildTracker(IEnumerable<string> projectFiles)
    {
        _projects = projectFiles
            .Where(p => !p.EndsWith(".shproj", StringComparison.OrdinalIgnoreCase))
            .Select(Path.GetFileNameWithoutExtension)
            .OfType<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Progress = new SolutionBuildProgress(0, _projects.Count, null);
    }

    public SolutionBuildProgress Progress { get; private set; }

    /// <summary>Takes one line of the build's output.</summary>
    /// <returns>Whether it moved the progress on: a project of the solution finished that had not before.</returns>
    public bool Observe(string line)
    {
        var arrow = line.IndexOf(Arrow, StringComparison.Ordinal);
        if (arrow <= 0)
        {
            return false;
        }

        var name = line[..arrow].Trim();
        if (!_projects.Contains(name) || !_done.Add(name))
        {
            return false;
        }

        Progress = Progress with { Done = _done.Count, Last = name };
        return true;
    }
}
