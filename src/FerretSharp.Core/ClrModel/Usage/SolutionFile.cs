using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace FerretSharp.Core.ClrModel;

/// <summary>
/// The solution a linked project belongs to and its projects (WP-40, ADR 0030), read without MSBuild: <c>.sln</c> from its
/// <c>Project(…)</c> lines, <c>.slnx</c> from its <c>&lt;Project Path&gt;</c> elements.
/// </summary>
public static partial class SolutionFile
{
    /// <summary>
    /// The solutions next to the project or further up that contain it: the first folder with at least one wins. Several
    /// means the user chooses; none means the project is read alone.
    /// </summary>
    public static IReadOnlyList<string> Find(string projectFile)
    {
        var project = Path.GetFullPath(projectFile);
        for (var directory = new DirectoryInfo(Path.GetDirectoryName(project)!); directory is not null; directory = directory.Parent)
        {
            var found = Candidates(directory.FullName)
                .Where(s => Contains(s, project))
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (found.Count > 0)
            {
                return found;
            }
        }

        return [];
    }

    /// <summary>The full paths of the solution's projects that exist (solution folders and missing projects left out).</summary>
    /// <exception cref="IOException">The solution could not be read.</exception>
    /// <exception cref="UnauthorizedAccessException">The solution could not be read.</exception>
    /// <exception cref="System.Xml.XmlException">An <c>.slnx</c> is no XML.</exception>
    public static IReadOnlyList<string> Projects(string solutionFile)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(solutionFile))!;
        var paths = solutionFile.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase)
            ? XDocument.Load(solutionFile).Descendants()
                .Where(e => e.Name.LocalName == "Project")
                .Select(e => (string?)e.Attribute("Path"))
                .OfType<string>()
            : File.ReadLines(solutionFile)
                .Select(l => ProjectLine().Match(l))
                .Where(m => m.Success)
                .Select(m => m.Groups["path"].Value);
        return paths
            .Where(p => p.EndsWith("proj", StringComparison.OrdinalIgnoreCase))
            .Select(p => Path.GetFullPath(Path.Combine(directory, p.Replace('\\', Path.DirectorySeparatorChar))))
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IEnumerable<string> Candidates(string directory)
    {
        try
        {
            return Directory.EnumerateFiles(directory, "*.sln")
                .Concat(Directory.EnumerateFiles(directory, "*.slnx"))
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static bool Contains(string solution, string project)
    {
        try
        {
            return Projects(solution).Contains(project, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            return false;
        }
    }

    /// <summary><c>Project("{FAE04EC0-…}") = "Shop.Data", "src\Shop.Data\Shop.Data.csproj", "{…}"</c>.</summary>
    [GeneratedRegex("""^\s*Project\("[^"]*"\)\s*=\s*"[^"]*"\s*,\s*"(?<path>[^"]+)"\s*,""")]
    private static partial Regex ProjectLine();
}
