using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FerretSharp.Core.IO;

namespace FerretSharp.Core.ClrModel;

/// <summary>A model read from the cache and when the model host exported it.</summary>
public sealed record CachedModel(ModelExport Model, DateTimeOffset ExportedAt);

/// <summary>
/// The exported models of linked projects (WP-16), one file per link, reused as long as the build output is unchanged:
/// connecting then needs no model host run. The fingerprint covers the link, the UI culture (enum display texts), the
/// export format and the model host itself, and name, size and write time of every assembly and JSON file of the build
/// output (referenced projects and satellite assemblies included; package versions are in the deps.json). Trusted as is
/// (decision of the user) – "Neu laden" and "Neu bauen" bypass it.
/// </summary>
public sealed class ModelCache(string directory, string modelHostPath, CultureInfo? culture = null)
{
    /// <summary>Version of the cache file itself; older files are ignored.</summary>
    private const int FileVersion = 1;

    private static readonly string[] Extensions = [".dll", ".exe", ".json"];

    public string Directory { get; } = directory;

    /// <summary>The cached model if it was exported from exactly this build output; null otherwise (also if unreadable).</summary>
    public async Task<CachedModel?> TryLoadAsync(ClrProjectLink link, BuildOutput output, CancellationToken cancellationToken)
    {
        var path = FileOf(link);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(path);
            var entry = await JsonSerializer.DeserializeAsync<CacheFile>(stream, ModelHostResult.JsonOptions, cancellationToken);
            return entry is { Version: FileVersion, Model.FormatVersion: ModelExport.CurrentFormatVersion }
                   && entry.Fingerprint == Fingerprint(link, output)
                ? new CachedModel(entry.Model, entry.ExportedAt)
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null; // a broken or locked file: the model host runs as without a cache
        }
    }

    /// <summary>Stores a freshly exported model; failures to write are ignored (the cache is only a shortcut).</summary>
    public async Task SaveAsync(ClrProjectLink link, BuildOutput output, ModelExport model, CancellationToken cancellationToken)
    {
        try
        {
            await AtomicJsonFile.WriteAsync(FileOf(link), new CacheFile(FileVersion, Fingerprint(link, output), DateTimeOffset.Now, model),
                ModelHostResult.JsonOptions, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // the cache is only a shortcut
        }
    }

    /// <summary>A hash over everything the exported model depends on.</summary>
    internal string Fingerprint(ClrProjectLink link, BuildOutput output)
    {
        var text = new StringBuilder()
            .Append("link|").Append(Normalize(link.ProjectFile)).Append('|').Append(link.Configuration).Append('|').Append(link.ContextType).Append('\n')
            .Append("culture|").Append((culture ?? CultureInfo.CurrentUICulture).Name).Append('\n')
            .Append("format|").Append(ModelExport.CurrentFormatVersion).Append('\n')
            .Append("host|").Append(Stamp(modelHostPath)).Append('\n');

        var folder = Path.GetDirectoryName(output.Assembly)!;
        IEnumerable<string> files = System.IO.Directory.Exists(folder)
            ? System.IO.Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                .Where(f => Extensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                .Select(f => Path.GetRelativePath(folder, f))
                .Order(StringComparer.OrdinalIgnoreCase)
            : [];
        foreach (var file in files)
        {
            text.Append(file.ToUpperInvariant()).Append('|').Append(Stamp(Path.Combine(folder, file))).Append('\n');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    /// <summary>One file per link: a hash of the project file and configuration as name.</summary>
    private string FileOf(ClrProjectLink link)
    {
        var key = $"{Normalize(link.ProjectFile)}|{link.Configuration}|{link.ContextType}";
        return Path.Combine(Directory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..32] + ".json");
    }

    private static string Normalize(string path) => Path.GetFullPath(path).ToUpperInvariant();

    private static string Stamp(string file)
    {
        var info = new FileInfo(file);
        return info.Exists ? $"{info.Length}|{info.LastWriteTimeUtc.Ticks}" : "missing";
    }

    private sealed record CacheFile(int Version, string Fingerprint, DateTimeOffset ExportedAt, ModelExport Model);
}
