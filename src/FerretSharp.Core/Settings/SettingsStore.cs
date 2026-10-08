using System.Text.Json;
using FerretSharp.Core.Connections;
using FerretSharp.Core.IO;

namespace FerretSharp.Core.Settings;

public sealed class SettingsStoreException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Persists <see cref="AppSettings"/> as a versioned JSON document. Writes are atomic (temp file + move).</summary>
public sealed class SettingsStore(string filePath)
{
    public const int CurrentVersion = 1;

    private readonly SemaphoreSlim _gate = new(1, 1);

    public string FilePath { get; } = filePath;

    /// <summary>Defaults if the file does not exist yet.</summary>
    /// <exception cref="SettingsStoreException">The file is broken or from a newer version.</exception>
    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(FilePath))
            {
                return AppSettings.Default;
            }

            await using var stream = File.OpenRead(FilePath);
            var document = await JsonSerializer.DeserializeAsync<SettingsDocument>(stream, JsonFiles.Options, cancellationToken);
            if (document?.Settings is null)
            {
                throw new SettingsStoreException($"{FilePath} ist leer.");
            }

            if (document.Version > CurrentVersion)
            {
                throw new SettingsStoreException($"{FilePath} stammt aus einer neueren FerretSharp-Version (Format {document.Version}).");
            }

            return document.Settings;
        }
        catch (JsonException ex)
        {
            throw new SettingsStoreException($"{FilePath} ist keine gültige Einstellungsdatei: {ex.Message}", ex);
        }
        finally
        {
            _gate.Release();
        }
    }

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
