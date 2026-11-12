using System.Security.Cryptography;
using FerretSharp.Core.IO;
using FerretSharp.Core.Resources;

namespace FerretSharp.Core.ClrModel;

/// <summary>What happened to one file of a plan.</summary>
public enum FileWriteOutcome
{
    Written,
    Created,

    /// <summary>Not written: the file changed since the analysis (decision of the user), or a new file exists now.</summary>
    Changed,

    /// <summary>Not written because another file of the plan could not be – a plan is written whole or not at all.</summary>
    Skipped,

    Failed,
}

/// <param name="Message">Why it was not written; null if it was.</param>
public sealed record FileWriteResult(string Relative, FileWriteOutcome Outcome, string? Message = null)
{
    public bool Succeeded => Outcome is FileWriteOutcome.Written or FileWriteOutcome.Created;
}

/// <summary>
/// Writes a plan into the work clone (WP-41, ADR 0050). Every file is checked before any is written: strictly below the
/// work clone, no link on the way (a junction or symbolic link could lead out of it), a changed file still with the bytes
/// it was analysed with, a new file not there yet. Only then are the files written, each into a temporary file beside it
/// first and moved over (<see cref="AtomicFile.WriteInUserFolderAsync{T}"/>; a new file without replacing one that came
/// up meanwhile). Nothing is built and nothing committed.
/// </summary>
public static class CodeChangeWriter
{
    public static async Task<IReadOnlyList<FileWriteResult>> WriteAsync(CodeChangePlan plan, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!plan.CanWrite)
        {
            throw new InvalidOperationException("A plan with problems or without files is not written.");
        }

        var refused = plan.Files.Select(f => (File: f, Reason: Refusal(plan.Root, f))).Where(f => f.Reason is not null).ToList();
        if (refused.Count > 0)
        {
            return [.. plan.Files.Select(f => refused.FirstOrDefault(r => r.File == f) is { File: not null } r
                ? new FileWriteResult(f.Relative, r.Reason!.Value.Outcome, r.Reason.Value.Message)
                : new FileWriteResult(f.Relative, FileWriteOutcome.Skipped, CodeEditText.Write_Skipped))];
        }

        var results = new List<FileWriteResult>();
        foreach (var file in plan.Files)
        {
            // Cancelling counts until the first file is written, then the plan is written whole: closing the view stopped it
            // between two files – a property without its configuration (review 3.70.0, A8).
            var token = results.Count == 0 ? cancellationToken : CancellationToken.None;
            token.ThrowIfCancellationRequested();
            try
            {
                var bytes = file.Format.Encode(file.NewText);
                if (file.IsNew)
                {
                    await CreateAsync(file.Path, bytes, token);
                    results.Add(new FileWriteResult(file.Relative, FileWriteOutcome.Created));
                }
                else
                {
                    await AtomicFile.WriteInUserFolderAsync(file.Path, async (stream, ct) =>
                    {
                        await stream.WriteAsync(bytes, ct);
                        return true;
                    }, token);
                    results.Add(new FileWriteResult(file.Relative, FileWriteOutcome.Written));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                results.Add(new FileWriteResult(file.Relative, FileWriteOutcome.Failed, ex.Message));
            }
        }

        return results;
    }

    /// <summary>Why a file may not be written now; null if it may.</summary>
    internal static (FileWriteOutcome Outcome, string Message)? Refusal(string root, FileChange file)
    {
        if (!Below(root, file.Path))
        {
            return (FileWriteOutcome.Failed, TextFormat.Format(CodeEditText.Write_OutsideRoot, file.Path));
        }

        try
        {
            if (file.IsNew)
            {
                return File.Exists(file.Path) || Directory.Exists(file.Path) ? (FileWriteOutcome.Changed, CodeEditText.Write_ExistsNow) : null;
            }

            if (CodeScan.IsLink(file.Path))
            {
                return (FileWriteOutcome.Failed, TextFormat.Format(CodeEditText.Write_Link, file.Relative));
            }

            var now = File.ReadAllBytes(file.Path);
            if (!SHA256.HashData(now).AsSpan().SequenceEqual(file.Original!.Hash))
            {
                return (FileWriteOutcome.Changed, CodeEditText.Write_ChangedMeanwhile);
            }

            // Read-only or locked shows now, before any file is written – not halfway through the plan (review WP-41).
            if (File.GetAttributes(file.Path).HasFlag(FileAttributes.ReadOnly))
            {
                return (FileWriteOutcome.Failed, CodeEditText.Write_ReadOnly);
            }

            // Without sharing: the move over it needs delete access, which a program holding it open without FILE_SHARE_DELETE
            // refuses only at the move – halfway through the plan (second review of WP-41).
            using (new FileStream(file.Path, FileMode.Open, FileAccess.Write, FileShare.None))
            {
            }

            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (FileWriteOutcome.Failed, ex.Message);
        }
    }

    /// <summary>
    /// The path lies strictly below the root, and no folder between them is a link: the scan skipped links, so a path through
    /// one did not come from it.
    /// </summary>
    internal static bool Below(string root, string path)
    {
        if (!UserFolderPaths.IsBelow(root, path))
        {
            return false; // also a drive as root (S:\), review WP-41
        }

        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var full = Path.GetFullPath(path);
        for (var folder = Path.GetDirectoryName(full); folder is not null && folder.Length > fullRoot.Length; folder = Path.GetDirectoryName(folder))
        {
            if (Directory.Exists(folder) && CodeScan.IsLink(folder))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>A new file: written beside it under a name of its own, then moved – never over a file that came up meanwhile.</summary>
    private static async Task CreateAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        var temp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize: 1 << 16, useAsync: true))
            {
                await stream.WriteAsync(bytes, cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temp, path, overwrite: false);
        }
        catch
        {
            try
            {
                File.Delete(temp);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // left behind: a .tmp beside the file
            }

            throw;
        }
    }
}
