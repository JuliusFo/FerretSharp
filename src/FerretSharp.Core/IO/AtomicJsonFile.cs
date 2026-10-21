using System.Text.Json;

namespace FerretSharp.Core.IO;

/// <summary>
/// Writes a JSON file so that a reader never sees half of it (<see cref="AtomicFile"/>): if writing fails (disk full,
/// cancelled, a value that cannot be serialized), the old file stays as it was (R2: was in six stores).
/// </summary>
public static class AtomicJsonFile
{
    /// <summary>Writes <paramref name="value"/> to <paramref name="path"/>, creating its directory if needed.</summary>
    public static Task WriteAsync<T>(string path, T value, JsonSerializerOptions options, CancellationToken cancellationToken) =>
        AtomicFile.WriteAsync(path, async (stream, ct) =>
        {
            await JsonSerializer.SerializeAsync(stream, value, options, ct);
            return true;
        }, cancellationToken);
}
