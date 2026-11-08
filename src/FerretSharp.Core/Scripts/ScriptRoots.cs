using FerretSharp.Core.Audit;
using FerretSharp.Core.Connections;
using FerretSharp.Core.IO;
using FerretSharp.Core.Settings;

namespace FerretSharp.Core.Scripts;

/// <summary>Which of the three script folders a root is (WP-77, decision of the user 2026-11-08).</summary>
public enum ScriptRootKind
{
    /// <summary>The user's own scripts: <c>scripts\</c> in the data folder, or <see cref="AppSettings.SqlScriptsPath"/>.</summary>
    Personal,

    /// <summary>Scripts of a product: the folder of a connection group (<see cref="AppSettings.SqlScriptsGroups"/>).</summary>
    Group,

    /// <summary>Scripts for any Oracle database (locks, sessions): <see cref="AppSettings.SqlScriptsGeneralPath"/>.</summary>
    General,
}

/// <summary>A folder of SQL scripts.</summary>
/// <param name="Path">The folder as set (trimmed); the personal one need not exist yet (the first save creates it).</param>
/// <param name="Group">The group as the settings name it (<see cref="ScriptRootKind.Group"/>), else null.</param>
public sealed record ScriptRoot(ScriptRootKind Kind, string Path, string? Group = null)
{
    /// <summary>The same root as a link names it: kind and group (the path may have been changed in the settings).</summary>
    public bool Is(ScriptRootKind kind, string? group) =>
        Kind == kind && (kind != ScriptRootKind.Group || ConnectionSearch.SameGroup(Group, group));
}

/// <summary>The script folders of the settings (WP-77).</summary>
public static class ScriptRoots
{
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>
    /// The folders a connection sees, in the explorer's order: personal, its group's (if set), general. A folder set twice
    /// is listed once, the first time.
    /// </summary>
    /// <param name="defaultPersonal">The personal folder in the data folder (<see cref="AppPaths.ScriptsDirectory"/>).</param>
    public static IReadOnlyList<ScriptRoot> For(AppSettings settings, string defaultPersonal, ConnectionProfile? profile)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var roots = new List<ScriptRoot> { Personal(settings, defaultPersonal) };
        if (AuditTemplateSource.GroupEntry(settings.SqlScriptsGroups, profile?.Group) is { } entry)
        {
            roots.Add(new ScriptRoot(ScriptRootKind.Group, entry.Value.Trim(), entry.Key));
        }

        if (PathInput.Normalize(settings.SqlScriptsGeneralPath) is { } general)
        {
            roots.Add(new ScriptRoot(ScriptRootKind.General, general));
        }

        return Distinct(roots);
    }

    /// <summary>Every folder of the settings – personal, those of all groups, general – each once (the library reads them all).</summary>
    public static IReadOnlyList<ScriptRoot> All(AppSettings settings, string defaultPersonal)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var roots = new List<ScriptRoot> { Personal(settings, defaultPersonal) };
        roots.AddRange(settings.SqlScriptsGroups
            .Where(e => PathInput.Normalize(e.Value) is not null)
            .OrderBy(e => e.Key, StringComparer.CurrentCultureIgnoreCase)
            .Select(e => new ScriptRoot(ScriptRootKind.Group, PathInput.Normalize(e.Value)!, e.Key)));
        if (PathInput.Normalize(settings.SqlScriptsGeneralPath) is { } general)
        {
            roots.Add(new ScriptRoot(ScriptRootKind.General, general));
        }

        return roots;
    }

    /// <summary>The root a link names (kind and group) among those of a connection; null if it has no such root (any more).</summary>
    public static ScriptRoot? Find(IEnumerable<ScriptRoot> roots, ScriptRootKind kind, string? group) =>
        roots.FirstOrDefault(r => r.Is(kind, group));

    /// <summary>Two roots in the same folder (a script there is the same file).</summary>
    public static bool SameFolder(string a, string b) =>
        PathComparer.Equals(Full(a), Full(b));

    private static ScriptRoot Personal(AppSettings settings, string defaultPersonal) =>
        new(ScriptRootKind.Personal, PathInput.Normalize(settings.SqlScriptsPath) ?? defaultPersonal);

    private static List<ScriptRoot> Distinct(List<ScriptRoot> roots)
    {
        var result = new List<ScriptRoot>();
        foreach (var root in roots.Where(root => !result.Any(r => SameFolder(r.Path, root.Path))))
        {
            result.Add(root);
        }

        return result;
    }

    private static string Full(string path)
    {
        try
        {
            return System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }
}
