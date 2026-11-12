using System.Diagnostics;
using FerretSharp.Core.IO;
using FerretSharp.Core.Resources;

namespace FerretSharp.Core.ClrModel;

/// <summary>Where a change is written and what speaks against it (WP-41): the work clone, its branch and the warnings.</summary>
/// <param name="Branch">The branch checked out in the work clone; null if it cannot be read.</param>
/// <param name="Warnings">The clone is on the branch of a connection, or a file has uncommitted changes – shown, not blocking.</param>
public sealed record CodeTargetCheck(string? Branch, IReadOnlyList<string> Warnings);

/// <summary>
/// The checks before writing into a work clone (WP-41, decisions of the user): never the linked clone of a connection
/// (it must match its database and is pulled) – a warning when the work clone has the branch one of them has checked out;
/// a warning for a file with uncommitted changes, asked from <c>git status</c>. Without git that check says so instead.
/// </summary>
public static class CodeTarget
{
    private static readonly TimeSpan GitLimit = TimeSpan.FromSeconds(15);

    /// <param name="linkedProjectFiles">The projects linked to the connections of the group: their clones' branches.</param>
    public static async Task<CodeTargetCheck> CheckAsync(CodeChangePlan plan, IEnumerable<string> linkedProjectFiles, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(linkedProjectFiles);
        var warnings = new List<string>();
        var branch = GitFolder.Branch(plan.Root);
        var linked = linkedProjectFiles.Select(GitFolder.FindRoot).OfType<string>()
            .Where(r => !string.Equals(Path.GetFullPath(r), Path.GetFullPath(plan.Root), StringComparison.OrdinalIgnoreCase))
            .Select(GitFolder.Branch).OfType<string>()
            .ToHashSet(StringComparer.Ordinal);
        if (branch is not null && linked.Contains(branch))
        {
            warnings.Add(TextFormat.Format(CodeEditText.Warning_ConnectionBranch, branch));
        }

        var changed = plan.Files.Where(f => !f.IsNew).Select(f => f.Relative).ToList();
        if (changed.Count > 0)
        {
            var status = await DirtyAsync(plan.Root, changed, cancellationToken);
            warnings.AddRange(status switch
            {
                { Found: false } => [CodeEditText.Warning_NoGit],
                { Dirty: null } => [CodeEditText.Warning_GitFailed], // safe.directory, no repository, too slow (review WP-41)
                _ => status.Dirty.Select(f => TextFormat.Format(CodeEditText.Warning_Uncommitted, f)),
            });
        }

        return new CodeTargetCheck(branch, warnings);
    }

    /// <summary>What <c>git status</c> said.</summary>
    /// <param name="Found">git is installed.</param>
    /// <param name="Dirty">The files it reports as changed; null if it did not answer (an error, not in time).</param>
    public sealed record GitStatus(bool Found, IReadOnlyList<string>? Dirty);

    /// <summary>
    /// Which of the files (relative to <paramref name="root"/>) <c>git status</c> reports as changed. <c>core.fsmonitor</c>
    /// is switched off – a repository's config could name a program for it –, optional locks too, so the index is not
    /// written while the user works in the clone, and the paths are taken literally, not as patterns (<c>[Kunde].cs</c>).
    /// </summary>
    public static async Task<GitStatus> DirtyAsync(string root, IReadOnlyList<string> relative, CancellationToken cancellationToken)
    {
        if (ExecutableSearch.FindOnPath(OperatingSystem.IsWindows() ? "git.exe" : "git") is not { } git)
        {
            return new GitStatus(false, null);
        }

        var start = new ProcessStartInfo(git)
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // Paths come as UTF-8 bytes with -z: read in the console's code page, "Größe.cs" matched no file (review 3.70.0, A12).
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        // Untracked files count too: an entity written by an earlier change and not committed yet (review 3.70.0, A11).
        foreach (var argument in (string[])["--literal-pathspecs", "-c", "core.fsmonitor=false", "status", "--porcelain=v1", "-z", "--untracked-files=all", "--"])
        {
            start.ArgumentList.Add(argument);
        }

        foreach (var path in relative)
        {
            start.ArgumentList.Add(path);
        }

        start.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        using var process = new Process { StartInfo = start };
        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new GitStatus(false, null);
        }

        ChildProcesses.Started(process);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(GitLimit);
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(limit.Token);
            _ = process.StandardError.ReadToEndAsync(limit.Token);
            await process.WaitForExitAsync(limit.Token);
            if (process.ExitCode != 0)
            {
                return new GitStatus(true, null);
            }

            var entries = StatusPaths(await output);
            return new GitStatus(true, [.. relative.Where(r => entries.Any(e => e == r.Replace('\\', '/') || e.EndsWith("/" + r.Replace('\\', '/'), StringComparison.Ordinal)))]);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Kill(process);
            return new GitStatus(true, null);
        }
        catch
        {
            Kill(process);
            throw;
        }
    }

    /// <summary>
    /// The paths of <c>git status --porcelain=v1 -z</c>: <c>XY path</c> per entry; a rename or copy (<c>R</c>, <c>C</c>)
    /// is followed by its old path as a field of its own, which is no entry (review 3.70.0, A11: it was cut like one).
    /// </summary>
    internal static IReadOnlyList<string> StatusPaths(string output)
    {
        var fields = output.Split('\0');
        var paths = new List<string>();
        for (var i = 0; i < fields.Length; i++)
        {
            var field = fields[i];
            if (field.Length <= 3)
            {
                continue;
            }

            paths.Add(field[3..].Replace('\\', '/'));
            if (field[0] is 'R' or 'C' || field[1] is 'R' or 'C')
            {
                i++; // the old path
            }
        }

        return paths;
    }

    private static void Kill(Process process)
    {
        try
        {
            process.Kill();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // gone already
        }
    }
}
