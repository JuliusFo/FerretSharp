namespace FerretSharp.Core.ClrModel;

/// <summary>
/// What FerretSharp reads from a Git working copy, without a Git library (WP-40, ADR 0030): its root and the checked-out
/// branch. A worktree's <c>.git</c> is a file pointing to its own folder (<c>gitdir: …</c>).
/// </summary>
public static class GitFolder
{
    /// <summary>The working copy containing <paramref name="path"/> (the folder with <c>.git</c>); null outside one.</summary>
    public static string? FindRoot(string path)
    {
        var start = Directory.Exists(path) ? path : Path.GetDirectoryName(Path.GetFullPath(path));
        for (var directory = start is null ? null : new DirectoryInfo(start); directory is not null; directory = directory.Parent)
        {
            var git = Path.Combine(directory.FullName, ".git");
            if (Directory.Exists(git) || File.Exists(git))
            {
                return directory.FullName;
            }
        }

        return null;
    }

    /// <summary>
    /// The branch checked out in the working copy at <paramref name="root"/> (<c>feature/xyz</c>), the first seven characters
    /// of the commit when detached; null if it cannot be read.
    /// </summary>
    public static string? Branch(string root)
    {
        try
        {
            if (GitDirectory(root) is not { } git || !File.Exists(Path.Combine(git, "HEAD")))
            {
                return null;
            }

            var head = File.ReadAllText(Path.Combine(git, "HEAD")).Trim();
            const string reference = "ref: refs/heads/";
            if (head.StartsWith(reference, StringComparison.Ordinal))
            {
                return head[reference.Length..];
            }

            return head.Length >= 7 && head.All(Uri.IsHexDigit) ? head[..7] : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The <c>.git</c> folder, or for a worktree (or submodule) the folder its <c>.git</c> file names.</summary>
    private static string? GitDirectory(string root)
    {
        var git = Path.Combine(root, ".git");
        if (Directory.Exists(git))
        {
            return git;
        }

        if (!File.Exists(git))
        {
            return null;
        }

        const string prefix = "gitdir:";
        var line = File.ReadLines(git).FirstOrDefault(l => l.StartsWith(prefix, StringComparison.Ordinal));
        if (line is null)
        {
            return null;
        }

        var target = line[prefix.Length..].Trim();
        var full = Path.GetFullPath(Path.IsPathRooted(target) ? target : Path.Combine(root, target));

        // A share the working copy is not on (review 3.44.0, A31): a folder unpacked from an archive would make FerretSharp
        // sign in to a foreign host every few seconds (the branch is read by a timer). A clone on a share may keep its
        // worktree data there.
        return IsShare(full) && !string.Equals(Path.GetPathRoot(full), Path.GetPathRoot(Path.GetFullPath(root)), StringComparison.OrdinalIgnoreCase)
            ? null
            : full;
    }

    private static bool IsShare(string path) => path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal);
}
