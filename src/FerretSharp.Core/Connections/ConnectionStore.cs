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
            var read = await VersionedJsonFile.ReadAsync<ConnectionsDocument>(FilePath, CurrentVersion, JsonFiles.Options, cancellationToken);
            // Anything but a readable file of this version refuses: the manager then never overwrites it.
            return read.State switch
            {
                JsonFileState.Missing => [],
                JsonFileState.Ok when read.Document!.Connections?.Any(p => p is null || p.Address is null || p.Name is null || p.User is null) == true =>
                    throw new ConnectionStoreException(TextFormat.Format(ConnectionText.InvalidFile, FilePath, ConnectionText.IncompleteConnection)),
                JsonFileState.Ok => read.Document!.Connections ?? [],
                JsonFileState.Empty => throw new ConnectionStoreException(TextFormat.Format(CoreText.FileEmpty, FilePath)),
                JsonFileState.Newer => throw new ConnectionStoreException(TextFormat.Format(CoreText.FileFromNewerVersion, FilePath, read.Version)),
                _ => throw new ConnectionStoreException(TextFormat.Format(ConnectionText.InvalidFile, FilePath, read.Error!.Message), read.Error),
            };
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
