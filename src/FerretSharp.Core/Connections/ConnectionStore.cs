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

    /// <exception cref="ConnectionStoreException">
    /// The file cannot be read, is no valid connections file or comes from a newer version – every such case, so the
    /// manager refuses to overwrite it (review 3.31.0, A6: an address without "type", a missing list or an I/O error got
    /// through, the list was empty, and the first new connection replaced all the others).
    /// </exception>
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
            using var json = await JsonDocument.ParseAsync(stream, default, cancellationToken);
            var root = json.RootElement;

            // The version first: a newer file may hold shapes this version cannot read (a new address type).
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("version", out var version)
                && version.TryGetInt32(out var number) && number > CurrentVersion)
            {
                throw new ConnectionStoreException(TextFormat.Format(ConnectionText.FileFromNewerVersion, FilePath, number));
            }

            var document = root.Deserialize<ConnectionsDocument>(JsonFiles.Options)
                           ?? throw new ConnectionStoreException(TextFormat.Format(ConnectionText.FileEmpty, FilePath));
            if (document.Connections?.Any(p => p is null || p.Address is null || p.Name is null || p.User is null) == true)
            {
                throw new ConnectionStoreException(TextFormat.Format(ConnectionText.InvalidFile, FilePath, ConnectionText.IncompleteConnection));
            }

            return document.Connections ?? [];
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            // NotSupportedException: an address without "type", or with "type" not first (edited by hand).
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

    private sealed record ConnectionsDocument(int Version, IReadOnlyList<ConnectionProfile>? Connections);
}
