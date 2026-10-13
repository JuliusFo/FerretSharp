using System.Text.Json;

namespace FerretSharp.Core.IO;

/// <summary>What reading one of FerretSharp's own JSON files found (<see cref="VersionedJsonFile.ReadAsync{T}"/>).</summary>
public enum JsonFileState
{
    /// <summary>There is no file.</summary>
    Missing,

    /// <summary>Read, and in a version this FerretSharp knows.</summary>
    Ok,

    /// <summary>JSON, but no document in it (<c>null</c>, or without the part the store needs).</summary>
    Empty,

    /// <summary>No JSON, or a shape that does not fit (a value of the wrong type, an address without <c>type</c>).</summary>
    Broken,

    /// <summary>The file could not be read (access, locked by a virus scanner or a backup).</summary>
    Unreadable,

    /// <summary>Written by a newer FerretSharp (its <c>version</c> is higher): not read, since it may hold shapes this one does not know.</summary>
    Newer,
}

/// <summary>A read of a versioned JSON file: its state, the document (only <see cref="JsonFileState.Ok"/>) and why not.</summary>
/// <param name="Version">The <c>version</c> in the file; null if it has none or it was not read.</param>
/// <param name="Error">What went wrong for <see cref="JsonFileState.Broken"/> and <see cref="JsonFileState.Unreadable"/>.</param>
public sealed record JsonFileRead<T>(JsonFileState State, T? Document, int? Version, Exception? Error = null) where T : class;

/// <summary>
/// Reading FerretSharp's own versioned JSON files (connections, recent connections, settings, workspaces, SQL history,
/// saved comparisons) one way – each store decides explicitly what a state means for it (R5b, review 3.31.0 B2: eight
/// copies with six rules; settings, workspaces and comparisons took a newer file with an unknown shape for a broken one,
/// the recent connections had no version, and a hand edit could still crash the start, A6).
/// </summary>
public static class VersionedJsonFile
{
    /// <summary>
    /// Reads <paramref name="path"/>. The version first: above <paramref name="currentVersion"/> the file is
    /// <see cref="JsonFileState.Newer"/> without deserializing it. A document <paramref name="complete"/> rejects (or null)
    /// is <see cref="JsonFileState.Empty"/>; a complete one goes through <paramref name="normalize"/> (collections set to
    /// null by hand become empty) and is <see cref="JsonFileState.Ok"/>.
    /// </summary>
    public static async Task<JsonFileRead<T>> ReadAsync<T>(string path, int currentVersion, JsonSerializerOptions options, CancellationToken cancellationToken,
        Func<T, bool>? complete = null, Func<T, T>? normalize = null)
        where T : class
    {
        if (!File.Exists(path))
        {
            return new(JsonFileState.Missing, null, null);
        }

        int? version = null;
        try
        {
            await using var stream = File.OpenRead(path);
            using var json = await JsonDocument.ParseAsync(stream, default, cancellationToken);
            var root = json.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("version", out var number) && number.TryGetInt32(out var found))
            {
                version = found;
                if (found > currentVersion)
                {
                    return new(JsonFileState.Newer, null, found);
                }
            }

            var document = root.Deserialize<T>(options);
            return document is null || complete?.Invoke(document) == false
                ? new(JsonFileState.Empty, null, version)
                : new(JsonFileState.Ok, normalize is null ? document : normalize(document), version);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            // NotSupportedException: polymorphic JSON without "type", or with "type" not first (edited by hand).
            return new(JsonFileState.Broken, null, version, ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new(JsonFileState.Unreadable, null, version, ex);
        }
    }

    /// <summary>
    /// Copies a file a store could not use next to it (<c>settings.json.bad-20261011-153000</c>) before it is replaced –
    /// an identical copy kept earlier is reused (every start with the same file would add one). The path of the copy.
    /// </summary>
    /// <exception cref="IOException">The file could not be copied.</exception>
    /// <exception cref="UnauthorizedAccessException">The file could not be copied.</exception>
    public static string KeepCopy(string path, DateTimeOffset now)
    {
        var content = File.ReadAllBytes(path);
        var folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
        if (Directory.EnumerateFiles(folder, Path.GetFileName(path) + ".bad-*").FirstOrDefault(f => File.ReadAllBytes(f).AsSpan().SequenceEqual(content)) is { } kept)
        {
            return kept;
        }

        var copy = $"{path}.bad-{now:yyyyMMdd-HHmmss}";
        File.Copy(path, copy, overwrite: false);
        return copy;
    }
}
