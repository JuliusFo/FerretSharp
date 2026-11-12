using System.Text.Json;
using System.Text.Json.Nodes;
using FerretSharp.Core.Connections;
using FerretSharp.Core.IO;
using FerretSharp.Core.Resources;

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
                case JsonFileState.Broken when read.Error is JsonException error && await WithoutUnreadableAsync(error, cancellationToken) is { } partly:
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
    /// The settings without those the serializer could not read: each error names its setting in its path
    /// (<c>$.settings.theme</c>, also <c>$.settings.shortcuts.x</c> for a value inside one); that setting is left out and the
    /// rest read again, until it works. Null if an error lies outside a single setting – then the file stays broken.
    /// </summary>
    private async Task<SettingsRead?> WithoutUnreadableAsync(JsonException first, CancellationToken cancellationToken)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(await File.ReadAllTextAsync(FilePath, cancellationToken));
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }

        if (root is not JsonObject { } document || document["settings"] is not JsonObject settings)
        {
            return null;
        }

        var skipped = new List<string>();
        for (var error = first; ;)
        {
            if (SettingOf(error.Path) is not { } name
                || settings.FirstOrDefault(p => string.Equals(p.Key, name, StringComparison.OrdinalIgnoreCase)).Key is not { } key)
            {
                return null;
            }

            settings.Remove(key);
            skipped.Add(key);
            try
            {
                return document.Deserialize<SettingsDocument>(JsonFiles.Options)?.Settings is { } read ? new(Normalized(read, skipped), skipped) : null;
            }
            catch (JsonException next)
            {
                error = next;
            }
            catch (NotSupportedException)
            {
                return null;
            }
        }
    }

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
