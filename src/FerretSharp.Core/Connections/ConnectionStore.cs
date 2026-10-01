using System.Text.Json;
using System.Text.Json.Serialization;

namespace FerretSharp.Core.Connections;

public interface IConnectionStore
{
    Task<IReadOnlyList<ConnectionProfile>> LoadAsync(CancellationToken cancellationToken);

    Task SaveAsync(IReadOnlyList<ConnectionProfile> profiles, CancellationToken cancellationToken);
}

public sealed class ConnectionStoreException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Persists connection profiles as a versioned JSON document. Writes are atomic (temp file + move).</summary>
public sealed class ConnectionStore(string filePath) : IConnectionStore
{
    public const int CurrentVersion = 1;

    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly SemaphoreSlim _gate = new(1, 1);

    public string FilePath { get; } = filePath;

    public async Task<IReadOnlyList<ConnectionProfile>> LoadAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(FilePath))
            {
                return [];
            }

            await using var stream = File.OpenRead(FilePath);
            var document = await JsonSerializer.DeserializeAsync<ConnectionsDocument>(stream, JsonOptions, cancellationToken)
                           ?? throw new ConnectionStoreException($"{FilePath} ist leer.");

            if (document.Version > CurrentVersion)
            {
                throw new ConnectionStoreException(
                    $"{FilePath} stammt aus einer neueren FerretSharp-Version (Format {document.Version}).");
            }

            return document.Connections;
        }
        catch (JsonException ex)
        {
            throw new ConnectionStoreException($"{FilePath} ist kein gültiges Verbindungs-JSON: {ex.Message}", ex);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(IReadOnlyList<ConnectionProfile> profiles, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(FilePath))!);

            var tempFile = FilePath + ".tmp";
            await using (var stream = File.Create(tempFile))
            {
                await JsonSerializer.SerializeAsync(stream, new ConnectionsDocument(CurrentVersion, profiles), JsonOptions, cancellationToken);
            }

            File.Move(tempFile, FilePath, overwrite: true);
        }
        finally
        {
            _gate.Release();
        }
    }

    private sealed record ConnectionsDocument(int Version, IReadOnlyList<ConnectionProfile> Connections);
}
