namespace FerretSharp.Core.IO;

/// <summary>
/// Writes a file so that nobody sees half of it: into a temporary file beside it first, then moved over the file. If
/// writing fails or is cancelled, a file already there stays as it was and the temporary file is removed (R2 for the JSON
/// stores; WP-54 for exports, which take minutes and can be cancelled).
/// </summary>
public static class AtomicFile
{
    /// <summary>
    /// Writes <paramref name="path"/> through <paramref name="write"/>, creating its directory if needed. The temporary file
    /// is <c>file.tmp</c>: a store owns its folder, and the next write replaces one left behind by a crash.
    /// </summary>
    public static Task<T> WriteAsync<T>(string path, Func<Stream, CancellationToken, Task<T>> write, CancellationToken cancellationToken) =>
        WriteAsync(path, path + ".tmp", FileMode.Create, write, cancellationToken);

    /// <summary>
    /// An export to a file the user chose (WP-54): like <see cref="WriteAsync{T}"/>, but the target is checked first – a file
    /// open in Excel would otherwise fail only at the move, after minutes of reading – and the temporary file gets a name
    /// of its own, so a file of the user's called <c>file.tmp</c> is never replaced or removed.
    /// </summary>
    /// <exception cref="IOException">The target exists and cannot be replaced (open in another program).</exception>
    /// <exception cref="UnauthorizedAccessException">The target or its folder cannot be written.</exception>
    public static Task<T> WriteExportAsync<T>(string path, Func<Stream, CancellationToken, Task<T>> write, CancellationToken cancellationToken)
    {
        if (File.Exists(path))
        {
            // Opening it for writing without sharing fails like the move would: in use, or read-only. Nothing is changed.
            using (new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None))
            {
            }
        }

        return WriteAsync(path, $"{path}.{Guid.NewGuid():N}.tmp", FileMode.CreateNew, write, cancellationToken);
    }

    private static async Task<T> WriteAsync<T>(
        string path, string temp, FileMode mode, Func<Stream, CancellationToken, Task<T>> write, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        try
        {
            T result;
            await using (var stream = new FileStream(temp, mode, FileAccess.Write, FileShare.None, bufferSize: 1 << 16, useAsync: true))
            {
                result = await write(stream, cancellationToken);
                // On disk before the move: after a power loss NTFS may keep the rename but not the data – an empty file (R4a).
                stream.Flush(flushToDisk: true);
            }

            File.Move(temp, path, overwrite: true);
            return result;
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // still locked: the next write replaces it
        }
    }
}
