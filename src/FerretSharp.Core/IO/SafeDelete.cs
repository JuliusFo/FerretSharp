namespace FerretSharp.Core.IO;

/// <summary>
/// The only way FerretSharp and its tests delete a folder with its contents: only strictly below a fixed root, never the
/// root itself, a drive root or a relative path. Guards against the classic accident of a path built from an empty or
/// wrong value (<c>rm -rf $DIR/</c>) – a violation is a programming error and throws before anything is deleted.
/// </summary>
public static class SafeDelete
{
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>Deletes <paramref name="path"/> with its contents; a missing folder is nothing to do.</summary>
    /// <param name="root">The fixed folder the caller works in, e.g. <c>%TEMP%\FerretSharp\modelhost</c>.</param>
    /// <exception cref="InvalidOperationException"><paramref name="path"/> is not strictly below <paramref name="root"/>.</exception>
    public static void DirectoryBelow(string root, string path)
    {
        var checkedPath = CheckBelow(root, path);
        if (Directory.Exists(checkedPath))
        {
            Directory.Delete(checkedPath, recursive: true);
        }
    }

    /// <summary>Like <see cref="DirectoryBelow"/>, but a folder still in use stays where it is (false).</summary>
    /// <exception cref="InvalidOperationException"><paramref name="path"/> is not strictly below <paramref name="root"/>.</exception>
    public static bool TryDirectoryBelow(string root, string path)
    {
        try
        {
            DirectoryBelow(root, path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>The full path of <paramref name="path"/> if it lies strictly below <paramref name="root"/>.</summary>
    /// <exception cref="InvalidOperationException">It does not, or <paramref name="root"/> is a drive root.</exception>
    public static string CheckBelow(string root, string path)
    {
        if (!Path.IsPathFullyQualified(root) || !Path.IsPathFullyQualified(path))
        {
            throw new InvalidOperationException($"Refusing to delete '{path}': root '{root}' and path must be absolute.");
        }

        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (string.Equals(fullRoot, Path.GetPathRoot(fullRoot), PathComparison))
        {
            throw new InvalidOperationException($"Refusing to delete '{path}': root '{root}' is a drive root.");
        }

        if (!fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, PathComparison))
        {
            throw new InvalidOperationException($"Refusing to delete '{path}': it is not below '{root}'.");
        }

        return fullPath;
    }
}
