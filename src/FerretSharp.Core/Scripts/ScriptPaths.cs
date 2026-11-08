using FerretSharp.Core.Connections;
using FerretSharp.Core.IO;
using FerretSharp.Core.Resources;

namespace FerretSharp.Core.Scripts;

/// <summary>
/// Paths of scripts inside their root (WP-77). A script is named by its root and a path relative to it with <c>/</c>
/// between the parts (<c>Locks/Sessions.sql</c>) – what a workspace keeps, never the absolute path. Reading, writing and
/// deleting go only through <see cref="Resolve"/>: strictly below the root, no part a link (symbolic link, junction) –
/// the folders may be shared repositories, and a path from a workspace file or a link in one must not reach out of them.
/// </summary>
public static class ScriptPaths
{
    /// <summary>The extension of a script file.</summary>
    public const string Extension = ".sql";

    /// <summary>How deep folders are followed below a root.</summary>
    public const int MaxDepth = 8;

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>
    /// Why <paramref name="name"/> cannot be the name of a script or folder as typed in the save dialog: empty, a character
    /// Windows refuses (also <c>%</c> and bidi controls), a trailing dot or blank, a device name like <c>CON</c>, a leading
    /// dot (hidden, as <c>.git</c>, not listed). Null if it can.
    /// </summary>
    public static string? NameProblem(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return ScriptText.NameMissing;
        }

        if (name.StartsWith('.'))
        {
            return TextFormat.Format(ScriptText.NameLeadingDot, name);
        }

        return SuggestedFileName.Of(name) == name ? null : TextFormat.Format(ScriptText.NameInvalid, name);
    }

    /// <summary>The relative path of a script named <paramref name="name"/> (without extension) in <paramref name="folder"/> ("" for the root).</summary>
    public static string Of(string folder, string name)
    {
        var file = name.EndsWith(Extension, StringComparison.OrdinalIgnoreCase) ? name : name + Extension;
        return folder.Length == 0 ? file : folder.TrimEnd('/') + "/" + file;
    }

    /// <summary>The parts of a relative path (folders, then the file).</summary>
    public static IReadOnlyList<string> Parts(string relative) =>
        relative.Split(['/', '\\'], StringSplitOptions.None);

    /// <summary>The folder of a relative path, "" for the root.</summary>
    public static string FolderOf(string relative)
    {
        var parts = Parts(relative);
        return string.Join('/', parts.Take(parts.Count - 1));
    }

    /// <summary>The script's name: the file name without <c>.sql</c>.</summary>
    public static string NameOf(string relative)
    {
        var file = Parts(relative)[^1];
        return file.EndsWith(Extension, StringComparison.OrdinalIgnoreCase) ? file[..^Extension.Length] : file;
    }

    /// <summary>
    /// Checks a folder below a root as typed in the save dialog (<c>Locks/Oracle</c>, "" for the root): each part a name
    /// (<see cref="NameProblem"/>), at most <see cref="MaxDepth"/> deep. Returns it with <c>/</c> between the parts.
    /// </summary>
    /// <exception cref="RefusedException">A part cannot be a folder name.</exception>
    public static string Folder(string? folder)
    {
        var text = (folder ?? "").Trim().Trim('/', '\\');
        if (text.Length == 0)
        {
            return "";
        }

        var parts = Parts(text).Select(p => p.Trim()).ToList();
        if (parts.Count > MaxDepth)
        {
            throw new RefusedException(TextFormat.Format(ScriptText.FolderTooDeep, text, MaxDepth));
        }

        foreach (var part in parts)
        {
            if (NameProblem(part) is { } problem)
            {
                throw new RefusedException(problem);
            }
        }

        return string.Join('/', parts);
    }

    /// <summary>
    /// The full path of the script <paramref name="relative"/> below <paramref name="root"/>, checked: a relative path of
    /// names (<see cref="NameProblem"/>) ending in <c>.sql</c>, at most <see cref="MaxDepth"/> folders deep, strictly below
    /// the root, and no part of it below the root a link – the root itself may be one (the user chose it).
    /// </summary>
    /// <exception cref="RefusedException">The path does not meet one of these.</exception>
    public static string Resolve(string root, string relative)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(relative);
        if (!Path.IsPathFullyQualified(root))
        {
            throw new RefusedException(TextFormat.Format(ScriptText.RootNotAbsolute, root));
        }

        var parts = Parts(relative);
        if (relative.Length == 0 || Path.IsPathRooted(relative) || parts.Count > MaxDepth + 1
            || parts.Any(p => NameProblem(p) is not null)
            || !parts[^1].EndsWith(Extension, StringComparison.OrdinalIgnoreCase))
        {
            throw new RefusedException(TextFormat.Format(ScriptText.PathInvalid, relative));
        }

        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var full = Path.GetFullPath(Path.Combine([fullRoot, .. parts]));
        if (!full.StartsWith(Below(fullRoot), PathComparison))
        {
            throw new RefusedException(TextFormat.Format(ScriptText.PathOutsideRoot, relative, root));
        }

        // Each part that exists, from the first below the root to the file: none may lead elsewhere.
        var current = fullRoot;
        foreach (var part in parts)
        {
            current = Path.Combine(current, part);
            if (IsLink(current))
            {
                throw new RefusedException(TextFormat.Format(ScriptText.PathThroughLink, relative, root));
            }
        }

        return full;
    }

    /// <summary>The relative path of <paramref name="full"/> below <paramref name="root"/>, with <c>/</c>; null if it is not below it.</summary>
    public static string? RelativeOf(string root, string full)
    {
        var prefix = Below(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)));
        var path = Path.GetFullPath(full);
        return path.StartsWith(prefix, PathComparison) && path.Length > prefix.Length
            ? path[prefix.Length..].Replace(Path.DirectorySeparatorChar, '/')
            : null;
    }

    /// <summary>
    /// What every path below <paramref name="fullRoot"/> starts with: the root and a separator – a drive keeps its own
    /// (<c>S:\</c>), which <see cref="Path.TrimEndingDirectorySeparator(string)"/> does not remove (review WP-77, M2).
    /// </summary>
    private static string Below(string fullRoot) =>
        Path.EndsInDirectorySeparator(fullRoot) ? fullRoot : fullRoot + Path.DirectorySeparatorChar;

    /// <summary>
    /// A file or folder that is a symbolic link or junction; false for one that does not exist. Other reparse points (files
    /// of OneDrive kept only online) are no links: <see cref="FileSystemInfo.LinkTarget"/> names only links.
    /// </summary>
    public static bool IsLink(string path)
    {
        try
        {
            FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
            return info.Exists && info.LinkTarget is not null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return true; // cannot be looked at: not followed
        }
    }
}
