using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FerretSharp.Core.IO;

namespace FerretSharp.Core.ClrModel;

/// <summary>One project of the solution and how it went into the index.</summary>
/// <param name="Uses">What was read from its assembly; null if it is not built or could not be read.</param>
/// <param name="Error">Why it could not be read (project file or assembly); null otherwise.</param>
/// <param name="ReachesModel">
/// It defines entity types or references a project that does (through other projects of the solution, too): only then can
/// its code use them. True if that is not known (project file unreadable, no entity type found in the solution).
/// </param>
public sealed record CodeUsageProject(string ProjectFile, ProjectBuild? Build, AssemblyUses? Uses, string? Error, bool ReachesModel = true)
{
    public string Name => Path.GetFileNameWithoutExtension(ProjectFile);
}

/// <param name="Solution">The solution read; null if the linked project was read alone (no solution found).</param>
public sealed record CodeUsageResult(string? Solution, IReadOnlyList<CodeUsageProject> Projects, CodeUsageIndex Index)
{
    /// <summary>
    /// Projects whose code could use the model but was not read (not built, or unreadable): "unused" may be wrong while there
    /// are any. Projects that cannot reach the entity types do not count – they cannot use them, built or not –, nor do test
    /// projects, whose uses "unused" leaves out anyway (known from the project file, <see cref="ProjectBuild.IsTestProject"/>).
    /// </summary>
    public IReadOnlyList<CodeUsageProject> Missing =>
        Projects.Where(p => p.ReachesModel && p.Uses is null && p.Build is not { IsTestProject: true }).ToList();
}

/// <summary>
/// Builds the code usage index of a linked project's solution (WP-40, ADR 0030): finds each project's assembly, reads it
/// with <see cref="IlUsageScanner"/> or takes the result from the cache (one file per assembly MVID, so after a build only
/// the changed assemblies are read again), and joins everything with the model export.
/// </summary>
public partial class CodeUsageBuilder(string cacheDirectory, TimeProvider? time = null)
{
    /// <summary>Cache files not used for this long are removed when an index is built.</summary>
    public static readonly TimeSpan CacheLifetime = TimeSpan.FromDays(30);

    /// <summary>
    /// Builds of one assembly kept in the cache, the most recently used: every build of a changed project writes a new file
    /// (review 3.44.0, A11) – switching between a few branches stays cheap, a day of builds does not pile up.
    /// </summary>
    public const int BuildsKeptPerAssembly = 3;

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public string CacheDirectory { get; } = cacheDirectory;

    /// <param name="solution">The solution to read (<see cref="SolutionFile.Find"/>); null = the linked project alone.</param>
    /// <exception cref="IOException">The solution could not be read.</exception>
    /// <exception cref="System.Xml.XmlException">An <c>.slnx</c> is no XML.</exception>
    public virtual async Task<CodeUsageResult> BuildAsync(ClrProjectLink link, string? solution, ModelExport model, CancellationToken cancellationToken)
    {
        var projects = solution is null ? [Path.GetFullPath(link.ProjectFile)] : SolutionFile.Projects(solution);
        var builds = projects.Select(p => Locate(p, link.Configuration)).ToList();
        var solutionAssemblies = builds.Select(b => b.Build?.AssemblyName).OfType<string>().ToList();

        var read = new CodeUsageProject[builds.Count];
        await Parallel.ForAsync(0, builds.Count, new ParallelOptions { CancellationToken = cancellationToken, MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2) },
            async (i, ct) => read[i] = await ReadAsync(builds[i], solutionAssemblies, ct));

        PruneCache();
        var index = CodeUsageIndex.Build(model, read.Select(p => p.Uses).OfType<AssemblyUses>().ToList());
        return new CodeUsageResult(solution, MarkModelReach(read, model), index);
    }

    /// <summary>
    /// Sets <see cref="CodeUsageProject.ReachesModel"/>: the projects whose assembly defines an entity type, and every project
    /// referencing one of them, directly or through others (<c>ProjectReference</c>, read without building – so it is known
    /// for projects not built, too). Without a project defining one (entities outside the solution, or not built), all may.
    /// </summary>
    internal static IReadOnlyList<CodeUsageProject> MarkModelReach(IReadOnlyList<CodeUsageProject> projects, ModelExport model)
    {
        var entityTypes = model.Entities.Select(e => e.ClrType).ToHashSet(StringComparer.Ordinal);
        var defining = projects
            .Where(p => p.Uses?.Types.Any(t => entityTypes.Contains(t.Type)) == true)
            .Select(p => p.ProjectFile)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (defining.Count == 0)
        {
            return projects;
        }

        var references = projects
            .GroupBy(p => p.ProjectFile, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Build?.References ?? [], StringComparer.OrdinalIgnoreCase);

        bool Reaches(string project, HashSet<string> seen) =>
            defining.Contains(project)
            || (seen.Add(project) && references.TryGetValue(project, out var referenced) && referenced.Any(r => Reaches(r, seen)));

        return projects
            .Select(p => p with { ReachesModel = p.Build is null || Reaches(p.ProjectFile, new HashSet<string>(StringComparer.OrdinalIgnoreCase)) })
            .ToList();
    }

    private static CodeUsageProject Locate(string project, string configuration)
    {
        try
        {
            return new CodeUsageProject(project, BuildOutputLocator.FindAssembly(project, configuration), null, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            return new CodeUsageProject(project, null, null, ex.Message);
        }
    }

    private async Task<CodeUsageProject> ReadAsync(CodeUsageProject project, IReadOnlyCollection<string> solutionAssemblies, CancellationToken cancellationToken)
    {
        if (project.Build?.Assembly is not { } assembly)
        {
            return project;
        }

        try
        {
            var mvid = IlUsageScanner.ReadMvid(assembly);
            var cacheFile = Path.Combine(CacheDirectory, CacheFileName(project.Build.AssemblyName ?? Path.GetFileNameWithoutExtension(assembly), mvid, solutionAssemblies));
            if (await TryLoadAsync(cacheFile, mvid, solutionAssemblies, cancellationToken) is { } cached)
            {
                return project with { Uses = cached };
            }

            var uses = IlUsageScanner.Scan(assembly, solutionAssemblies);
            await SaveAsync(cacheFile, uses, cancellationToken);
            return project with { Uses = uses };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // One odd assembly (deleted by a running build, unusual metadata or PDB) costs that project, not the index.
            return project with { Error = ex.Message };
        }
    }

    /// <summary>A cached result of the same build for the same set of solution assemblies; null otherwise (any doubt is a miss).</summary>
    private async Task<AssemblyUses?> TryLoadAsync(string path, Guid mvid, IReadOnlyCollection<string> solutionAssemblies, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            AssemblyUses? cached;
            await using (var stream = File.OpenRead(path))
            {
                cached = await JsonSerializer.DeserializeAsync<AssemblyUses>(stream, JsonFiles.Options, cancellationToken);
            }

            if (cached is not { FormatVersion: AssemblyUses.CurrentFormatVersion } || cached.Mvid != mvid
                || cached.Uses is null || cached.Types is null || cached.SolutionAssemblies is null
                || !cached.SolutionAssemblies.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(solutionAssemblies.Append(cached.AssemblyName)))
            {
                return null;
            }

            File.SetLastWriteTimeUtc(path, _time.GetUtcNow().UtcDateTime);
            return cached;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// <c>Assembly.mvid.set.json</c>: a result holds only the uses of the solution's types, so it is valid for this set of
    /// solution assemblies alone – two solutions on the same build kept overwriting each other's file (review 3.44.0, A27).
    /// </summary>
    internal static string CacheFileName(string assemblyName, Guid mvid, IReadOnlyCollection<string> solutionAssemblies)
    {
        var set = string.Join('|', solutionAssemblies.Select(a => a.ToUpperInvariant()).Order(StringComparer.Ordinal));
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(set)))[..8];
        return $"{assemblyName}.{mvid:N}.{hash}.json";
    }

    /// <summary>The assembly a cache file belongs to; null for a file of another form.</summary>
    private static string? AssemblyOf(string file) =>
        CacheFilePattern().Match(Path.GetFileName(file)) is { Success: true } match ? match.Groups[1].Value : null;

    [GeneratedRegex(@"^(.+)\.[0-9a-f]{32}\.[0-9a-f]{8}\.json$", RegexOptions.IgnoreCase)]
    private static partial Regex CacheFilePattern();

    /// <summary>Failures to write are ignored: the cache is only a shortcut.</summary>
    private static async Task SaveAsync(string path, AssemblyUses uses, CancellationToken cancellationToken)
    {
        try
        {
            await AtomicJsonFile.WriteAsync(path, uses, JsonFiles.Compact, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // the next build reads the assembly again
        }
    }

    /// <summary>
    /// Removes cache files of builds nobody has used for <see cref="CacheLifetime"/>, and of an assembly all but its
    /// <see cref="BuildsKeptPerAssembly"/> most recently used (only <c>*.json</c> directly in the cache folder).
    /// </summary>
    private void PruneCache()
    {
        try
        {
            if (!Directory.Exists(CacheDirectory))
            {
                return;
            }

            var limit = _time.GetUtcNow().UtcDateTime - CacheLifetime;
            var files = Directory.EnumerateFiles(CacheDirectory, "*.json", SearchOption.TopDirectoryOnly)
                .Select(f => (File: f, Used: File.GetLastWriteTimeUtc(f)))
                .ToList();
            var surplus = files
                .GroupBy(f => AssemblyOf(f.File), StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Key is not null)
                .SelectMany(g => g.OrderByDescending(f => f.Used).Skip(BuildsKeptPerAssembly));
            foreach (var (file, _) in files.Where(f => f.Used < limit).Concat(surplus).Distinct())
            {
                File.Delete(file);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // tried again next time
        }
    }
}
