using System.Text.RegularExpressions;

namespace FerretSharp.Core.ClrModel;

/// <summary>Whether a work clone fits a connection's linked project.</summary>
public enum WorkCloneFit
{
    /// <summary>The linked project is in the work clone under the same path relative to the repository root.</summary>
    Fits,

    /// <summary>The folder is gone.</summary>
    Missing,

    /// <summary>The linked project is not in a Git working copy: there is no relative path to compare.</summary>
    LinkedNotInRepository,

    /// <summary>The work clone has no project at that relative path (another repository, or moved).</summary>
    ProjectMissing,
}

/// <summary>Where a use's source file is opened: in the work clone or, failing that, in the linked (branch) clone.</summary>
public sealed record SourceLocation(string Path, bool InWorkClone);

/// <summary>
/// Maps paths of the linked clone (the PDB's paths come from its build) onto the active work clone (WP-40, ADR 0030).
/// Nothing is searched for: the user chooses the work clone, FerretSharp only checks that the linked project is there
/// under the same relative path.
/// </summary>
public static partial class WorkCloneMapping
{
    public static WorkCloneFit Check(string linkedProjectFile, string workClone)
    {
        if (!Directory.Exists(workClone))
        {
            return WorkCloneFit.Missing;
        }

        if (GitFolder.FindRoot(linkedProjectFile) is not { } linkedRoot)
        {
            return WorkCloneFit.LinkedNotInRepository;
        }

        return File.Exists(Path.Combine(workClone, Path.GetRelativePath(linkedRoot, Path.GetFullPath(linkedProjectFile))))
            ? WorkCloneFit.Fits
            : WorkCloneFit.ProjectMissing;
    }

    /// <summary>
    /// The file to open for a source file named in the linked clone's PDB: the same relative path in the work clone if it
    /// fits; else the file declaring <paramref name="typeName"/> in the work clone; else the file in the linked clone. Null
    /// if none exists. A deterministic build's path (<c>/_/src/…</c>, PathMap) is relative to the repository root.
    /// </summary>
    /// <param name="typeName">The type the use is written in (<c>Shop.Services.CustomerService</c>), for the fallback search.</param>
    public static SourceLocation? Resolve(string document, string linkedProjectFile, string? workClone, string? typeName, CancellationToken cancellationToken)
    {
        var linkedRoot = GitFolder.FindRoot(linkedProjectFile);
        var relative = Relative(document, linkedRoot);
        // "/_/…" is fully qualified on Linux, but it is no file there either.
        var linkedFile = DeterministicPath().IsMatch(document)
            ? linkedRoot is null ? null : Path.Combine(linkedRoot, relative!)
            : Path.IsPathFullyQualified(document) ? document : null;

        if (workClone is not null && Check(linkedProjectFile, workClone) == WorkCloneFit.Fits)
        {
            if (relative is not null && Path.Combine(workClone, relative) is var mapped && File.Exists(mapped))
            {
                return new SourceLocation(mapped, true);
            }

            if (typeName is not null && FindTypeFile(workClone, typeName, cancellationToken) is { } declared)
            {
                return new SourceLocation(declared, true);
            }
        }

        return linkedFile is not null && File.Exists(linkedFile) ? new SourceLocation(linkedFile, false) : null;
    }

    /// <summary>
    /// The first <c>.cs</c> file below <paramref name="root"/> declaring the type (by its simple name; nested types by the
    /// innermost): files named after the type first. <c>bin</c>, <c>obj</c> and dot folders are skipped.
    /// </summary>
    public static string? FindTypeFile(string root, string typeName, CancellationToken cancellationToken)
    {
        var simple = typeName.Split('.', '+')[^1];
        var tick = simple.IndexOf('`', StringComparison.Ordinal);
        if (tick > 0)
        {
            simple = simple[..tick];
        }

        var declaration = new Regex($@"\b(class|record|struct|interface|enum)\s+{Regex.Escape(simple)}\b", RegexOptions.CultureInvariant);
        var files = SourceFiles(root).ToList();
        foreach (var file in files.OrderBy(f => !string.Equals(Path.GetFileNameWithoutExtension(f), simple, StringComparison.OrdinalIgnoreCase)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (declaration.IsMatch(File.ReadAllText(file)))
                {
                    return file;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // a locked or vanished file
            }
        }

        return null;
    }

    /// <summary>The document's path relative to the repository root; null if it lies outside.</summary>
    private static string? Relative(string document, string? linkedRoot)
    {
        if (DeterministicPath().Match(document) is { Success: true } deterministic)
        {
            return deterministic.Groups[1].Value.Replace('/', Path.DirectorySeparatorChar);
        }

        if (linkedRoot is null || !Path.IsPathFullyQualified(document))
        {
            return null;
        }

        var relative = Path.GetRelativePath(linkedRoot, document);
        return relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathFullyQualified(relative) ? null : relative;
    }

    private static IEnumerable<string> SourceFiles(string root)
    {
        var pending = new Stack<string>([root]);
        while (pending.TryPop(out var directory))
        {
            string[] files, directories;
            try
            {
                files = Directory.GetFiles(directory, "*.cs");
                directories = Directory.GetDirectories(directory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var file in files)
            {
                yield return file;
            }

            foreach (var child in directories)
            {
                var name = Path.GetFileName(child);
                if (!name.StartsWith('.') && !name.Equals("bin", StringComparison.OrdinalIgnoreCase) && !name.Equals("obj", StringComparison.OrdinalIgnoreCase))
                {
                    pending.Push(child);
                }
            }
        }
    }

    [GeneratedRegex("^/_/(.+)$")]
    private static partial Regex DeterministicPath();
}
