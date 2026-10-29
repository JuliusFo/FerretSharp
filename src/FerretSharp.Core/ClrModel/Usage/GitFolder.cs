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

    /// <summary>
    /// The first seven characters of the commit checked out in the working copy at <paramref name="root"/> (WP-64: the origin
    /// of a model file); null if it cannot be read. Only files: the branch's ref, else <c>packed-refs</c>, both also in the
    /// common folder of a worktree.
    /// </summary>
    public static string? Commit(string root)
    {
        try
        {
            if (GitDirectory(root) is not { } git || !File.Exists(Path.Combine(git, "HEAD")))
            {
                return null;
            }

            var head = File.ReadAllText(Path.Combine(git, "HEAD")).Trim();
            const string reference = "ref: ";
            if (!head.StartsWith(reference, StringComparison.Ordinal))
            {
                return Short(head);
            }

            var name = head[reference.Length..];
            if (!name.StartsWith("refs/", StringComparison.Ordinal) || name.Contains("..", StringComparison.Ordinal) || name.Contains('\\') || name.Contains(':'))
            {
                return null; // only a ref below the git folder: no other file, no share
            }
            var common = File.Exists(Path.Combine(git, "commondir"))
                ? Path.GetFullPath(Path.Combine(git, File.ReadAllText(Path.Combine(git, "commondir")).Trim()))
                : git;
            if (!Followed(common, root))
            {
                common = git; // as for gitdir (A31): no sign-in to a foreign share
            }

            foreach (var folder in new[] { git, common }.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var file = Path.Combine(folder, name.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(file))
                {
                    return Short(File.ReadAllText(file).Trim());
                }

                var packed = Path.Combine(folder, "packed-refs");
                if (File.Exists(packed)
                    && File.ReadLines(packed).Select(l => l.Split(' ')).FirstOrDefault(p => p.Length == 2 && p[1] == name) is { } line)
                {
                    return Short(line[0]);
                }
            }

            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        static string? Short(string hash) => hash.Length >= 7 && hash.All(Uri.IsHexDigit) ? hash[..7] : null;
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
        return Followed(full, root) ? full : null;
    }

    /// <summary>A folder a working copy's files point to is read only if it is no share, or on the share the working copy is on.</summary>
    private static bool Followed(string folder, string root) =>
        !IsShare(folder) || string.Equals(Path.GetPathRoot(folder), Path.GetPathRoot(Path.GetFullPath(root)), StringComparison.OrdinalIgnoreCase);

    /// <summary>A path on another machine (UNC, also with forward slashes): touching it signs in there over SMB (NTLM).</summary>
    public static bool IsShare(string path) => path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal);
}
