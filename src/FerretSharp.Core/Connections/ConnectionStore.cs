using System.Text.Json.Serialization;
using System.Text.Json;
using FerretSharp.Core.IO;
using FerretSharp.Core.Resources;

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
            var document = await JsonSerializer.DeserializeAsync<ConnectionsDocument>(stream, JsonFiles.Options, cancellationToken)
                           ?? throw new ConnectionStoreException(TextFormat.Format(ConnectionText.FileEmpty, FilePath));

            if (document.Version > CurrentVersion)
            {
                throw new ConnectionStoreException(TextFormat.Format(ConnectionText.FileFromNewerVersion, FilePath, document.Version));
            }

            return document.Connections;
        }
        catch (JsonException ex)
        {
            throw new ConnectionStoreException(TextFormat.Format(ConnectionText.InvalidFile, FilePath, ex.Message), ex);
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
            await AtomicJsonFile.WriteAsync(FilePath, new ConnectionsDocument(CurrentVersion, profiles), JsonFiles.Options, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private sealed record ConnectionsDocument(int Version, IReadOnlyList<ConnectionProfile> Connections);
}
