using FerretSharp.Core.ClrModel;

namespace FerretSharp.UI.State;

/// <summary>A folder the work clone dialog offers (WP-40).</summary>
/// <param name="Branch">The branch checked out now; null if it cannot be read.</param>
/// <param name="Fit">Whether it fits the shown connection's linked project; null without a link.</param>
public sealed record WorkCloneChoice(string Path, string Name, string? Branch, WorkCloneFit? Fit, bool IsActive);

/// <summary>What the work clone dialog and the status bar show (WP-40, ADR 0030).</summary>
public static class WorkCloneChoices
{
    /// <summary>The group's active clone first (also if it is gone, so it can be switched away from), then the recent folders.</summary>
    public static IReadOnlyList<WorkCloneChoice> For(WorkClones clones, string? group, ClrProjectLink? link)
    {
        var active = clones.ActiveFor(group);
        var paths = clones.Recent.ToList();
        if (active is not null && !paths.Contains(active, StringComparer.OrdinalIgnoreCase))
        {
            paths.Insert(0, active);
        }

        return paths
            .OrderBy(p => !string.Equals(p, active, StringComparison.OrdinalIgnoreCase))
            .Select(p => Choice(p, link, string.Equals(p, active, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    /// <summary>The group's active clone as the status bar shows it; null if none is chosen.</summary>
    public static WorkCloneChoice? Active(WorkClones clones, string? group, ClrProjectLink? link) =>
        clones.ActiveFor(group) is { } active ? Choice(active, link, true) : null;

    /// <summary>The working copy of a picked solution or project file (its Git root, else its folder).</summary>
    public static string RootOf(string pickedFile) => GitFolder.FindRoot(pickedFile) ?? System.IO.Path.GetDirectoryName(pickedFile)!;

    private static WorkCloneChoice Choice(string path, ClrProjectLink? link, bool active) =>
        new(path, System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(path)), Directory.Exists(path) ? GitFolder.Branch(path) : null,
            link is null ? null : WorkCloneMapping.Check(link.ProjectFile, path), active);
}
