using System.Xml;
using System.Xml.Linq;

namespace FerretSharp.Core.ClrModel;

/// <summary>
/// The project a C# file of the work clone belongs to (WP-41): its project file, whether it is SDK-style, its
/// <c>&lt;Nullable&gt;</c>. One place since review 3.70.0, B5 – two walks to the nearest project file had different rules for a
/// folder with several (unknown here, the first in no fixed order there), and an SDK project was found by a text search.
/// </summary>
internal static class CodeProject
{
    /// <summary>
    /// The project files of the nearest folder with any, from <paramref name="folder"/> up to <paramref name="root"/>: empty
    /// if there is none, several if that folder has several (which one compiles the file is not known); null if a folder
    /// cannot be read.
    /// </summary>
    internal static IReadOnlyList<string>? Of(string root, string folder)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        for (var current = Path.GetFullPath(folder); current is not null && current.Length >= fullRoot.Length; current = Path.GetDirectoryName(current))
        {
            try
            {
                if (ProjectFiles(current) is { Length: > 0 } projects)
                {
                    return projects;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        return [];
    }

    /// <summary>
    /// An SDK-style project compiles every <c>.cs</c> file below it, an old one only those it lists: <c>Sdk</c> on the root,
    /// an <c>&lt;Sdk&gt;</c> element or an import with <c>Sdk</c>, read as XML. A file that cannot be read counts as SDK-style:
    /// no warning on a guess.
    /// </summary>
    internal static bool IsSdk(string project)
    {
        try
        {
            var root = XDocument.Load(project).Root;
            return root is null || root.Attribute("Sdk") is not null
                   || root.Elements().Any(e => e.Name.LocalName == "Sdk" || (e.Name.LocalName == "Import" && e.Attribute("Sdk") is not null));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException)
        {
            return true;
        }
    }

    /// <summary>
    /// <c>&lt;Nullable&gt;</c> of the project a folder belongs to: its project file, else the nearest <c>Directory.Build.props</c>
    /// above it, not above the work clone (the project file is read after the props and wins). True for <c>enable</c> and
    /// <c>annotations</c>, false for <c>disable</c> and <c>warnings</c>, null if neither says – then the files decide
    /// (review WP-41: a project with nullable reference types and no <c>string?</c> yet got none).
    /// </summary>
    internal static bool? Nullable(string root, string folder)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var projectSeen = false;
        for (var current = Path.GetFullPath(folder); current is not null && current.Length >= fullRoot.Length; current = Path.GetDirectoryName(current))
        {
            try
            {
                if (!projectSeen && ProjectFiles(current) is { Length: > 0 } projects)
                {
                    projectSeen = true;
                    if (projects.Length > 1)
                    {
                        return null; // which one owns the file is not known: the files decide
                    }

                    if (NullableSetting(File.ReadAllText(projects[0])) is { } setting)
                    {
                        return setting.Value;
                    }
                }

                // MSBuild imports only the nearest Directory.Build.props; one above it only if that one says so.
                var props = Path.Combine(current, "Directory.Build.props");
                if (File.Exists(props))
                {
                    return NullableSetting(File.ReadAllText(props))?.Value;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        return null;
    }

    /// <summary>
    /// The <c>&lt;Nullable&gt;</c> of a project or props file as MSBuild reads it: only from property groups without a
    /// condition, the last one wins, comments do not count (second review of WP-41: a regex read them). A setting under a
    /// condition, or a file that is no XML, gives an unknown value (<c>(null)</c>) – then the files decide; no setting at
    /// all gives null.
    /// </summary>
    private static (bool? Value, bool Unknown)? NullableSetting(string msbuild)
    {
        XDocument document;
        try
        {
            document = XDocument.Parse(msbuild);
        }
        catch (XmlException)
        {
            return (null, true);
        }

        var settings = document.Root?.Descendants().Where(e => e.Name.LocalName == "Nullable").ToList() ?? [];
        if (settings.Count == 0)
        {
            return null;
        }

        if (settings.Any(e => e.Attribute("Condition") is not null || e.Parent is not { Name.LocalName: "PropertyGroup" } group
                              || group.Attribute("Condition") is not null || group.Parent != document.Root))
        {
            return (null, true);
        }

        return (settings[^1].Value.Trim().ToLowerInvariant() switch
        {
            "enable" or "annotations" => true,
            "disable" or "warnings" => false,
            _ => null,
        }, false);
    }

    private static string[] ProjectFiles(string folder) => Directory.GetFiles(folder, "*.csproj");
}
