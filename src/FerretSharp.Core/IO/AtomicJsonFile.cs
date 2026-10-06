using System.Text.Json;

namespace FerretSharp.Core.IO;

/// <summary>
/// Writes a JSON file so that a reader never sees half of it: into <c>file.tmp</c> first, then moved over the file. If
/// writing fails (disk full, cancelled, a value that cannot be serialized), the old file stays as it was and the
/// temporary file is removed (R2: was in six stores).
/// </summary>
public static class AtomicJsonFile
{
    /// <summary>Writes <paramref name="value"/> to <paramref name="path"/>, creating its directory if needed.</summary>
    public static async Task WriteAsync<T>(string path, T value, JsonSerializerOptions options, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + ".tmp";
        try
        {
            await using (var stream = File.Create(temp))
            {
                await JsonSerializer.SerializeAsync(stream, value, options, cancellationToken);
            }

            File.Move(temp, path, overwrite: true);
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
