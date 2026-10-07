using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace FerretSharp.Core.ClrModel;

/// <summary>The .NET project linked to a connection (WP-11): the project with the DbContext.</summary>
/// <param name="ProjectFile">Full path of the <c>.csproj</c>.</param>
/// <param name="Configuration">Build configuration whose output is read.</param>
/// <param name="ContextType">DbContext type (full or short name) if the project has several; null = its only one.</param>
public sealed record ClrProjectLink(string ProjectFile, string Configuration = "Debug", string? ContextType = null)
{
    public string ProjectName => Path.GetFileNameWithoutExtension(ProjectFile);
}

/// <summary>A failure around the linked project with a message for the user (not built, wrong framework …).</summary>
public sealed class ClrModelException(string kind, string message, string? detail = null) : Exception(message)
{
    public string Kind { get; } = kind;

    public string? Detail { get; } = detail;

    public ModelHostError ToError() => new(Kind, Message, Detail);
}

/// <summary>Error kinds on FerretSharp's side, before or around the ModelHost run.</summary>
public static class ClrModelErrorKind
{
    public const string ProjectNotFound = "ProjectNotFound";
    public const string NotBuilt = "NotBuilt";
    public const string UnsupportedFramework = "UnsupportedFramework";
    public const string NoEfCore = "NoEfCore";
    public const string DotNetMissing = "DotNetMissing";
    public const string HostFailed = "HostFailed";
    public const string Timeout = "Timeout";
}

/// <summary>The build output the model is read from.</summary>
/// <param name="TargetFramework">Runtime version of the output, e.g. <c>8.0</c>.</param>
/// <param name="PackageFolders">NuGet package folders (where the deps.json's packages are).</param>
/// <param name="SharedFrameworks">Shared frameworks besides Microsoft.NETCore.App, e.g. Microsoft.AspNetCore.App.</param>
/// <param name="NewerSource">A source file changed after the build; null if the build is current.</param>
public sealed record BuildOutput(
    string Assembly,
    string DepsFile,
    Version TargetFramework,
    DateTime BuiltAtUtc,
    IReadOnlyList<string> PackageFolders,
    IReadOnlyList<string> SharedFrameworks,
    string? NewerSource)
{
    public bool IsStale => NewerSource is not null;
}

/// <summary>
/// Finds the build output of a linked project without building it: <c>bin/&lt;Configuration&gt;/&lt;tfm&gt;</c> (or an
/// artifacts folder), the deps.json, the NuGet package folders from <c>obj/project.assets.json</c>, and whether a source
/// file is newer than the build.
/// </summary>
public static partial class BuildOutputLocator
{
    /// <summary>EF Core 8 needs .NET 8; the model host is built for it (ADR 0009).</summary>
    public static readonly Version MinimumFramework = new(8, 0);

    /// <summary>
    /// Early check for the connection dialog: a message if the linked project cannot work (missing, no EF Core, too old),
    /// null if it can – also when it is not built yet, which is no reason to refuse the link. Does not scan the sources.
    /// </summary>
    public static string? Check(ClrProjectLink link)
    {
        try
        {
            Find(link, checkSources: false);
            return null;
        }
        catch (ClrModelException ex) when (ex.Kind == ClrModelErrorKind.NotBuilt)
        {
            return null;
        }
        catch (ClrModelException ex)
        {
            return ex.Message;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException or JsonException)
        {
            return $"Das Projekt lässt sich nicht lesen: {ex.Message}";
        }
    }

    public static BuildOutput Find(ClrProjectLink link, bool checkSources = true)
    {
        if (!File.Exists(link.ProjectFile))
        {
            throw new ClrModelException(ClrModelErrorKind.ProjectNotFound, $"Projektdatei nicht gefunden: {link.ProjectFile}");
        }

        var projectDirectory = Path.GetDirectoryName(Path.GetFullPath(link.ProjectFile))!;
        var project = ReadProject(link.ProjectFile);
        var assemblyName = project.AssemblyName ?? link.ProjectName;

        var deps = OutputDirectories(projectDirectory, link)
            .Where(Directory.Exists)
            .SelectMany(d => Directory.EnumerateFiles(d, assemblyName + ".deps.json", SearchOption.AllDirectories))
            .Where(f => File.Exists(Path.Combine(Path.GetDirectoryName(f)!, assemblyName + ".dll")))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault()
            ?? throw new ClrModelException(ClrModelErrorKind.NotBuilt,
                $"Kein Build von {assemblyName} ({link.Configuration}) gefunden – bitte das Projekt bauen.");

        var assembly = Path.Combine(Path.GetDirectoryName(deps)!, assemblyName + ".dll");
        Version framework;
        bool efCore;
        using (var stream = OpenShared(deps))
        using (var json = JsonDocument.Parse(stream))
        {
            framework = RuntimeTarget(json.RootElement);
            efCore = ReferencesEfCore(json.RootElement);
        }

        if (framework < MinimumFramework)
        {
            throw new ClrModelException(ClrModelErrorKind.UnsupportedFramework,
                $"{assemblyName} ist für .NET {framework} gebaut – FerretSharp liest Modelle ab EF Core 8 (.NET 8).");
        }

        if (!efCore)
        {
            throw new ClrModelException(ClrModelErrorKind.NoEfCore,
                $"{assemblyName} referenziert EF Core nicht – verknüpfe das Projekt, das den DbContext enthält (nicht nur die Entities).");
        }

        var builtAt = File.GetLastWriteTimeUtc(assembly);
        var newer = !checkSources ? null : SourceDirectories(projectDirectory, project)
            .SelectMany(SourceFiles)
            .Select(f => (File: f, At: File.GetLastWriteTimeUtc(f)))
            .Where(f => f.At > builtAt)
            .OrderByDescending(f => f.At)
            .Select(f => f.File)
            .FirstOrDefault();

        return new BuildOutput(assembly, deps, framework, builtAt, PackageFolders(projectDirectory), project.SharedFrameworks, newer);
    }

    private sealed record ProjectInfo(string? AssemblyName, IReadOnlyList<string> SharedFrameworks, IReadOnlyList<string> ProjectReferences);

    private static ProjectInfo ReadProject(string projectFile)
    {
        var document = XDocument.Load(projectFile);
        var elements = document.Descendants().ToList();
        var assemblyName = elements.FirstOrDefault(e => e.Name.LocalName == "AssemblyName" && !string.IsNullOrWhiteSpace(e.Value))?.Value.Trim();

        var frameworks = elements
            .Where(e => e.Name.LocalName == "FrameworkReference")
            .Select(e => (string?)e.Attribute("Include"))
            .OfType<string>()
            .ToList();
        if (((string?)document.Root?.Attribute("Sdk"))?.Contains("Microsoft.NET.Sdk.Web", StringComparison.OrdinalIgnoreCase) == true)
        {
            frameworks.Add("Microsoft.AspNetCore.App");
        }

        var references = elements
            .Where(e => e.Name.LocalName == "ProjectReference")
            .Select(e => (string?)e.Attribute("Include"))
            .OfType<string>()
            .ToList();
        return new ProjectInfo(assemblyName, frameworks.Distinct(StringComparer.OrdinalIgnoreCase).ToList(), references);
    }

    /// <summary><c>bin/&lt;Configuration&gt;</c>, or an artifacts layout (<c>UseArtifactsOutput</c>) further up.</summary>
    private static IEnumerable<string> OutputDirectories(string projectDirectory, ClrProjectLink link)
    {
        yield return Path.Combine(projectDirectory, "bin", link.Configuration);
        for (var directory = new DirectoryInfo(projectDirectory); directory is not null; directory = directory.Parent)
        {
            var artifacts = Path.Combine(directory.FullName, "artifacts", "bin", link.ProjectName);
            if (Directory.Exists(artifacts))
            {
                foreach (var output in Directory.EnumerateDirectories(artifacts)
                             .Where(d => Path.GetFileName(d).StartsWith(link.Configuration, StringComparison.OrdinalIgnoreCase)))
                {
                    yield return output;
                }

                yield break;
            }
        }
    }

    /// <summary>The deps.json lists <c>Microsoft.EntityFrameworkCore</c>; without it the host cannot load any EF type.</summary>
    private static bool ReferencesEfCore(JsonElement deps) =>
        deps.TryGetProperty("libraries", out var libraries)
        && libraries.EnumerateObject().Any(l => l.Name.StartsWith("Microsoft.EntityFrameworkCore/", StringComparison.OrdinalIgnoreCase));

    /// <summary><c>"runtimeTarget": { "name": ".NETCoreApp,Version=v8.0" }</c> → 8.0.</summary>
    private static Version RuntimeTarget(JsonElement deps)
    {
        var name = deps.TryGetProperty("runtimeTarget", out var target) && target.TryGetProperty("name", out var value)
            ? value.GetString() ?? ""
            : "";
        var match = FrameworkVersion().Match(name);
        if (!name.StartsWith(".NETCoreApp", StringComparison.Ordinal) || !match.Success)
        {
            throw new ClrModelException(ClrModelErrorKind.UnsupportedFramework,
                $"Unbekanntes Zielframework „{name}“ – FerretSharp liest Modelle ab EF Core 8 (.NET 8).");
        }

        return new Version(int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Opens a file that a build or restore may be writing right now without getting in its way: <c>File.OpenRead</c> denies
    /// writers while the file is open, so a <c>dotnet build</c> rewriting it at that moment would fail.
    /// </summary>
    private static FileStream OpenShared(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    /// <summary>From <c>obj/project.assets.json</c>; otherwise <c>NUGET_PACKAGES</c> or the user's default folder.</summary>
    private static IReadOnlyList<string> PackageFolders(string projectDirectory)
    {
        var assets = Path.Combine(projectDirectory, "obj", "project.assets.json");
        if (File.Exists(assets))
        {
            try
            {
                using var stream = OpenShared(assets);
                using var json = JsonDocument.Parse(stream);
                if (json.RootElement.TryGetProperty("packageFolders", out var folders))
                {
                    var result = folders.EnumerateObject().Select(p => p.Name).Where(Directory.Exists).ToList();
                    if (result.Count > 0)
                    {
                        return result;
                    }
                }
            }
            catch (JsonException)
            {
                // fall through to the defaults
            }
        }

        var fallback = Environment.GetEnvironmentVariable("NUGET_PACKAGES") is { Length: > 0 } configured
            ? configured
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
        return Directory.Exists(fallback) ? [fallback] : [];
    }

    /// <summary>The project and the projects it references (entities often live in another project).</summary>
    private static IEnumerable<string> SourceDirectories(string projectDirectory, ProjectInfo project)
    {
        yield return projectDirectory;
        foreach (var reference in project.ProjectReferences)
        {
            var path = Path.GetFullPath(Path.Combine(projectDirectory, reference.Replace('\\', Path.DirectorySeparatorChar)));
            if (Path.GetDirectoryName(path) is { } directory && Directory.Exists(directory))
            {
                yield return directory;
            }
        }
    }

    private static IEnumerable<string> SourceFiles(string directory) =>
        Directory.EnumerateFiles(directory, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            .Where(f => !IsBuildFolder(Path.GetRelativePath(directory, f)));

    private static bool IsBuildFolder(string relative)
    {
        var first = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
        return first.Equals("bin", StringComparison.OrdinalIgnoreCase) || first.Equals("obj", StringComparison.OrdinalIgnoreCase);
    }

    [GeneratedRegex(@"Version=v(\d+)\.(\d+)")]
    private static partial Regex FrameworkVersion();
}
