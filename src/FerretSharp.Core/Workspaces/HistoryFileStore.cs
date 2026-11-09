using FerretSharp.Core.IO;

namespace FerretSharp.Core.Workspaces;

/// <summary>
/// A history kept in one file per connection (<c>{directory}\{id}.json</c>), newest first: the SQL editor's statements
/// (<see cref="SqlHistoryStore"/>) and the conditions of table tabs (<see cref="ConditionHistoryStore"/>, WP-79). A file that
/// cannot be read for the moment (locked) or comes from a newer FerretSharp is not overwritten – a new entry is then only
/// returned, not stored. A broken one is kept as a copy (<c>….json.bad-…</c>) before a new history replaces it (R5b).
/// </summary>
public abstract class HistoryFileStore<TEntry>(string directory, int fileVersion)
    where TEntry : class
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public string Directory { get; } = directory;

    /// <summary>Newest first; empty if there is none or the file cannot be read.</summary>
    public async Task<IReadOnlyList<TEntry>> LoadAsync(Guid connectionId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return (await ReadAsync(connectionId, cancellationToken)).Entries;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <returns>The history with the new entry, stored if the file allows it.</returns>
    public async Task<IReadOnlyList<TEntry>> AddAsync(Guid connectionId, TEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var (stored, write) = await ReadAsync(connectionId, cancellationToken);
            var canWrite = write switch
            {
                Write.Replace => true,
                Write.AfterCopy => TryKeepCopy(FileOf(connectionId)),
                _ => false,
            };
            var entries = stored.ToList();
            Add(entries, entry);
            if (canWrite)
            {
                await AtomicJsonFile.WriteAsync(FileOf(connectionId), new HistoryFile(fileVersion, entries), JsonFiles.Options, cancellationToken);
            }

            return entries;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Puts <paramref name="entry"/> into <paramref name="entries"/> (newest first) and drops what is too old.</summary>
    protected abstract void Add(List<TEntry> entries, TEntry entry);

    /// <summary>Removes a connection's history (the connection was deleted).</summary>
    public void Delete(Guid connectionId)
    {
        try
        {
            File.Delete(FileOf(connectionId));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Whether a new history may replace the file.</summary>
    private enum Write
    {
        Replace,

        /// <summary>A broken history is no reason to stop working: kept as a copy, then a new file.</summary>
        AfterCopy,

        /// <summary>Locked for the moment (virus scanner, backup) – the next entry tries again – or a newer FerretSharp's history.</summary>
        No,
    }

    private async Task<(IReadOnlyList<TEntry> Entries, Write Write)> ReadAsync(Guid connectionId, CancellationToken cancellationToken)
    {
        var read = await VersionedJsonFile.ReadAsync<HistoryFile>(FileOf(connectionId), fileVersion, JsonFiles.Options, cancellationToken);
        return read.State switch
        {
            JsonFileState.Missing => ([], Write.Replace),
            JsonFileState.Ok when read.Version == fileVersion => (Normalize((read.Document!.Entries ?? []).OfType<TEntry>()), Write.Replace), // null by hand (A6)
            JsonFileState.Ok or JsonFileState.Empty or JsonFileState.Broken => ([], Write.AfterCopy), // no version or an unknown one, null, no JSON
            _ => ([], Write.No),
        };
    }

    /// <summary>The entries as read, without those a file edited by hand made unusable.</summary>
    protected virtual IReadOnlyList<TEntry> Normalize(IEnumerable<TEntry> entries) => entries.ToList();

    private static bool TryKeepCopy(string path)
    {
        try
        {
            VersionedJsonFile.KeepCopy(path, DateTimeOffset.Now);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false; // without a copy the file stays; the entry is only shown
        }
    }

    private string FileOf(Guid connectionId) => Path.Combine(Directory, connectionId.ToString("D") + ".json");

    private sealed record HistoryFile(int Version, IReadOnlyList<TEntry?>? Entries);
}
