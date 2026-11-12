using System.Text.Json;
using System.Text.Json.Nodes;
using FerretSharp.Core.Connections;
using FerretSharp.Core.IO;
using FerretSharp.Core.Resources;
using FerretSharp.Core.Schema;

namespace FerretSharp.Core.Settings;

public sealed class SettingsStoreException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Persists <see cref="AppSettings"/> as a versioned JSON document. Writes are atomic (temp file + move).</summary>
public sealed class SettingsStore(string filePath)
{
    public const int CurrentVersion = 1;

    private readonly SemaphoreSlim _gate = new(1, 1);

    public string FilePath { get; } = filePath;

    /// <summary>Defaults if the file does not exist yet; a setting it cannot read has its default (<see cref="ReadAsync"/>).</summary>
    /// <exception cref="SettingsStoreException">The file is empty, broken or from a newer version (R5b: a newer one with a shape this version does not know was "broken").</exception>
    /// <exception cref="IOException">The file cannot be read.</exception>
    /// <exception cref="UnauthorizedAccessException">The file cannot be read.</exception>
    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken) => (await ReadAsync(cancellationToken)).Settings;

    /// <summary>
    /// As <see cref="LoadAsync"/>, with the settings the file has but this version cannot read: a value it does not know (an
    /// enum value of a newer version, a hand edit) gives that one setting its default, the others stay (fix/robustness – before,
    /// the whole file was dropped). The names as in the file.
    /// </summary>
    /// <exception cref="SettingsStoreException">The file is empty, broken or from a newer version.</exception>
    /// <exception cref="IOException">The file cannot be read.</exception>
    /// <exception cref="UnauthorizedAccessException">The file cannot be read.</exception>
    public async Task<SettingsRead> ReadAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var read = await VersionedJsonFile.ReadAsync<SettingsDocument>(FilePath, CurrentVersion, JsonFiles.Options, cancellationToken,
                complete: document => document.Settings is not null);
            switch (read.State)
            {
                case JsonFileState.Missing:
                    return new(AppSettings.Default, []);
                case JsonFileState.Ok:
                    var skipped = new List<string>();
                    return new(Normalized(read.Document!.Settings!, skipped), skipped);
                case JsonFileState.Broken when read.Error is JsonException && WithoutUnreadable(read.Content) is { } partly:
                    return partly;
                case JsonFileState.Empty:
                    throw new SettingsStoreException(TextFormat.Format(CoreText.FileEmpty, FilePath));
                case JsonFileState.Newer:
                    throw new SettingsStoreException(TextFormat.Format(CoreText.FileFromNewerVersion, FilePath, read.Version));
                case JsonFileState.Unreadable:
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(read.Error!); // the caller keeps defaults without saving
                    break;
            }

            throw new SettingsStoreException(TextFormat.Format(SettingsText.InvalidFile, FilePath, read.Error!.Message), read.Error);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Copies a file <see cref="LoadAsync"/> could not read next to it (<c>settings.json.bad-20261011-153000</c>) before the
    /// first save replaces it with defaults (R4a: a broken file or one of a newer version lost every setting, shortcuts
    /// included, at the next save – moving a panel was enough). The path of the copy.
    /// </summary>
    /// <exception cref="IOException">The file could not be copied.</exception>
    /// <exception cref="UnauthorizedAccessException">The file could not be copied.</exception>
    public string KeepCopy(DateTimeOffset now) => VersionedJsonFile.KeepCopy(FilePath, now);

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await AtomicJsonFile.WriteAsync(FilePath, new SettingsDocument(CurrentVersion, settings), JsonFiles.Options, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// A value set to null by hand (review 3.31.0, A6): the default, not a crash at start. An enum given as a number the enum
    /// does not define (<c>"fkCounts": 3</c> – <see cref="System.Text.Json.Serialization.JsonStringEnumConverter"/> takes
    /// numbers unchecked): the default too, named in <paramref name="skipped"/>.
    /// </summary>
    private static AppSettings Normalized(AppSettings settings, List<string> skipped)
    {
        var normalized = settings with
        {
            Shortcuts = settings.Shortcuts ?? SettingsMap.Empty,
            ConnectionShortcuts = settings.ConnectionShortcuts ?? SettingsMap.Empty,
            PathAvoidedTables = settings.PathAvoidedTables ?? TableListMap.Empty,
            AuditTemplateGroups = settings.AuditTemplateGroups ?? SettingsMap.Empty,
            ReportExceptionsGroups = settings.ReportExceptionsGroups ?? SettingsMap.Empty,
            SqlScriptsGroups = settings.SqlScriptsGroups ?? SettingsMap.Empty,
        };
        foreach (var property in EnumSettings)
        {
            if (!Enum.IsDefined(property.PropertyType, property.GetValue(normalized)!))
            {
                property.SetValue(normalized, property.GetValue(AppSettings.Default)); // a copy of its own, made just above
                skipped.Add(JsonNamingPolicy.CamelCase.ConvertName(property.Name));
            }
        }

        return normalized;
    }

    private static readonly System.Reflection.PropertyInfo[] EnumSettings =
        typeof(AppSettings).GetProperties().Where(p => p.PropertyType.IsEnum && p.CanWrite).ToArray();

    /// <summary>
    /// The settings of a file the serializer could not read, without what it could not read (<paramref name="content"/> is the
    /// text <see cref="VersionedJsonFile"/> read). First an entry of a map that is no text (<c>"shortcuts": { "x": 5 }</c>):
    /// only that entry goes – its error names no setting in its path (<c>$.x</c>, the map's converter reads on its own) and
    /// would otherwise lose the whole file (code review). Then each error names its setting in its path (<c>$.settings.theme</c>);
    /// that setting is left out and the rest read again, until it works. Null if an error lies outside a single setting, or the
    /// text cannot be taken apart (a duplicate key throws <see cref="ArgumentException"/> only when the object is first used –
    /// it stopped the start, code review): then the file stays broken.
    /// </summary>
    private static SettingsRead? WithoutUnreadable(string? content)
    {
        if (content is null)
        {
            return null;
        }

        try
        {
            if (JsonNode.Parse(content) is not JsonObject document || document["settings"] is not JsonObject settings)
            {
                return null;
            }

            var skipped = new List<string>();
            foreach (var (name, value) in settings.ToList())
            {
                if (value is null || !MapSettings.TryGetValue(name, out var entryType))
                {
                    continue;
                }

                if (value is not JsonObject map)
                {
                    settings.Remove(name); // "shortcuts": [] fails with the path "$" (second review pass)
                    skipped.Add(name);
                    continue;
                }

                foreach (var (key, entry) in map.ToList())
                {
                    try
                    {
                        JsonSerializer.Deserialize(entry, entryType, JsonFiles.Options);
                    }
                    catch (JsonException)
                    {
                        map.Remove(key);
                        skipped.Add($"{name}.{key}");
                    }
                }
            }

            while (true)
            {
                try
                {
                    return document.Deserialize<SettingsDocument>(JsonFiles.Options)?.Settings is { } read ? new(Normalized(read, skipped), skipped) : null;
                }
                catch (JsonException error)
                {
                    if (SettingOf(error.Path) is not { } name
                        || settings.FirstOrDefault(p => string.Equals(p.Key, name, StringComparison.OrdinalIgnoreCase)).Key is not { } key)
                    {
                        return null;
                    }

                    settings.Remove(key); // one setting less each round: the loop ends
                    skipped.Add(key);
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// The settings that are maps read by a converter of their own (<see cref="SettingsMap"/>, <see cref="TableListMap"/>), by
    /// their name in the file (any case, as the serializer reads them), with the type of one entry – as the converter reads it.
    /// </summary>
    private static readonly Dictionary<string, Type> MapSettings = typeof(AppSettings).GetProperties()
        .Select(p => (Name: JsonNamingPolicy.CamelCase.ConvertName(p.Name),
            Entry: p.PropertyType == typeof(SettingsMap) ? typeof(string) : p.PropertyType == typeof(TableListMap) ? typeof(List<TableRef?>) : null))
        .Where(p => p.Entry is not null)
        .ToDictionary(p => p.Name, p => p.Entry!, StringComparer.OrdinalIgnoreCase);

    /// <summary>The setting a JSON path lies in: <c>theme</c> for <c>$.settings.theme</c>, <c>$.settings.theme.x</c> or <c>$.settings['theme']</c>.</summary>
    internal static string? SettingOf(string? path)
    {
        const string prefix = "$.settings";
        if (path is null || !path.StartsWith(prefix, StringComparison.Ordinal) || path.Length == prefix.Length)
        {
            return null;
        }

        var rest = path[prefix.Length..];
        if (rest.StartsWith("['", StringComparison.Ordinal))
        {
            var close = rest.IndexOf("']", StringComparison.Ordinal);
            return close > 2 ? rest[2..close] : null;
        }

        if (rest[0] != '.')
        {
            return null;
        }

        var end = rest.IndexOfAny(['.', '['], 1);
        var name = end < 0 ? rest[1..] : rest[1..end];
        return name.Length == 0 ? null : name;
    }

    private sealed record SettingsDocument(int Version, AppSettings Settings);
}

/// <summary>The settings of the file, and the settings in it this version could not read (they have their defaults).</summary>
public sealed record SettingsRead(AppSettings Settings, IReadOnlyList<string> Skipped);
