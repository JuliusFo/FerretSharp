namespace FerretSharp.Core.IO;

/// <summary>
/// Paths in a folder of the user's that FerretSharp writes into – the script folder (WP-77), the work clone (WP-41): what is
/// strictly below the root, and what is a link. One place since the review of WP-41 (the work clone had its own, older
/// copy of both mistakes WP-77 had fixed).
/// </summary>
public static class UserFolderPaths
{
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>
    /// What every path below <paramref name="fullRoot"/> starts with: the root and a separator – a drive keeps its own
    /// (<c>S:\</c>), which <see cref="Path.TrimEndingDirectorySeparator(string)"/> does not remove (review WP-77, M2).
    /// </summary>
    public static string Prefix(string fullRoot) =>
        Path.EndsInDirectorySeparator(fullRoot) ? fullRoot : fullRoot + Path.DirectorySeparatorChar;

    /// <summary>The full path lies strictly below the root (both made full first); false for a relative one.</summary>
    public static bool IsBelow(string root, string path)
    {
        if (!Path.IsPathFullyQualified(root) || !Path.IsPathFullyQualified(path))
        {
            return false;
        }

        var prefix = Prefix(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)));
        var full = Path.GetFullPath(path);
        return full.Length > prefix.Length && full.StartsWith(prefix, PathComparison);
    }

    /// <summary>
    /// A file or folder that is a symbolic link or junction; false for one that does not exist. Other reparse points (files
    /// of OneDrive kept only online, deduplicated files) are no links: <see cref="FileSystemInfo.LinkTarget"/> names only links.
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

    /// <summary>
    /// The <c>.cs</c> files below the root depth first – a folder's files, then its subfolders, each in ordinal order, the
    /// same on every system; <c>bin</c>, <c>obj</c>, dot folders and links skipped: a junction in the work clone could lead
    /// anywhere, only files really below it are read and written (WP-41; review 3.70.0, A4 and B2: "Usage" had its own walk
    /// that followed links).
    /// </summary>
    public static IEnumerable<string> SourceFiles(string root)
    {
        var pending = new Stack<string>([root]);
        while (pending.TryPop(out var directory))
        {
            string[] files, directories;
            try
            {
                files = Directory.GetFiles(directory, "*.cs");
                directories = Directory.GetDirectories(directory);
                Array.Sort(files, StringComparer.Ordinal);
                Array.Sort(directories, StringComparer.Ordinal);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var file in files)
            {
                if (!IsLink(file))
                {
                    yield return file;
                }
            }

            // pushed in reverse, so the stack pops them in ordinal order
            for (var i = directories.Length - 1; i >= 0; i--)
            {
                var child = directories[i];
                var name = Path.GetFileName(child);
                if (!name.StartsWith('.') && !name.Equals("bin", StringComparison.OrdinalIgnoreCase) && !name.Equals("obj", StringComparison.OrdinalIgnoreCase) && !IsLink(child))
                {
                    pending.Push(child);
                }
            }
        }
    }
}
