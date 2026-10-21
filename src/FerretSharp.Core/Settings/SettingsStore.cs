using System.Text.Json;
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

    /// <summary>Defaults if the file does not exist yet.</summary>
    /// <exception cref="SettingsStoreException">The file is empty, broken or from a newer version (R5b: a newer one with a shape this version does not know was "broken").</exception>
    /// <exception cref="IOException">The file cannot be read.</exception>
    /// <exception cref="UnauthorizedAccessException">The file cannot be read.</exception>
    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var read = await VersionedJsonFile.ReadAsync<SettingsDocument>(FilePath, CurrentVersion, JsonFiles.Options, cancellationToken,
                complete: document => document.Settings is not null);
            switch (read.State)
            {
                case JsonFileState.Missing:
                    return AppSettings.Default;
                case JsonFileState.Ok:
                    // A value set to null by hand (review 3.31.0, A6): the default, not a crash at start.
                    var settings = read.Document!.Settings!;
                    return settings with
                    {
                        Shortcuts = settings.Shortcuts ?? SettingsMap.Empty,
                        AuditTemplateGroups = settings.AuditTemplateGroups ?? SettingsMap.Empty,
                    };
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

    private sealed record SettingsDocument(int Version, AppSettings Settings);
}
