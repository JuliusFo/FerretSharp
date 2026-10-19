namespace FerretSharp.Core.IO;

/// <summary>
/// Writes a file so that nobody sees half of it: into <c>file.tmp</c> beside it first, then moved over the file. If writing
/// fails or is cancelled, a file already there stays as it was and the temporary file is removed (R2 for the JSON stores;
/// WP-54 for exports, which take minutes and can be cancelled).
/// </summary>
public static class AtomicFile
{
    /// <summary>Writes <paramref name="path"/> through <paramref name="write"/>, creating its directory if needed.</summary>
    public static async Task<T> WriteAsync<T>(string path, Func<Stream, CancellationToken, Task<T>> write, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + ".tmp";
        try
        {
            T result;
            await using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 1 << 16, useAsync: true))
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
