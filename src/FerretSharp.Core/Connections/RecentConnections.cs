using FerretSharp.Core.IO;

namespace FerretSharp.Core.Connections;

/// <summary>When each connection was last used; kept separate from connections.json so connecting never rewrites profiles.</summary>
public sealed class RecentConnections(string filePath, TimeProvider? timeProvider = null)
{
    public const int MaxEntries = 100;

    public const int CurrentVersion = 1;

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Dictionary<Guid, DateTimeOffset> _lastUsed = [];

    /// <summary>The file came from a newer FerretSharp: kept as it is, the history of this session stays in memory (R5b).</summary>
    private bool _keepFile;

    public IReadOnlyDictionary<Guid, DateTimeOffset> LastUsed => _lastUsed;

    /// <summary>Most recently used first, limited to ids that still exist.</summary>
    public IReadOnlyList<ConnectionProfile> MostRecent(IEnumerable<ConnectionProfile> profiles, int count) =>
        profiles
            .Where(p => _lastUsed.ContainsKey(p.Id))
            .OrderByDescending(p => _lastUsed[p.Id])
            .Take(count)
            .ToList();

    /// <summary>
    /// A broken or missing file just means "no history"; one of a newer FerretSharp too, but it is not overwritten (R5b: the
    /// version was not checked).
    /// </summary>
    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var read = await VersionedJsonFile.ReadAsync<Document>(filePath, CurrentVersion, JsonFiles.Options, cancellationToken);
            _keepFile = read.State == JsonFileState.Newer;
            // Entries missing, null or twice (edited by hand, review 3.31.0 A6): what is readable counts.
            _lastUsed = (read.Document?.Entries ?? []).OfType<Entry>()
                .GroupBy(e => e.Id)
                .ToDictionary(g => g.Key, g => g.Max(e => e.LastUsed));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task MarkUsedAsync(Guid profileId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var updated = new Dictionary<Guid, DateTimeOffset>(_lastUsed) { [profileId] = _time.GetUtcNow() };
            var entries = updated
                .OrderByDescending(kv => kv.Value)
                .Take(MaxEntries)
                .Select(kv => new Entry(kv.Key, kv.Value))
                .ToList();

            if (!_keepFile)
            {
                await AtomicJsonFile.WriteAsync(filePath, new Document(CurrentVersion, entries), JsonFiles.Options, cancellationToken);
            }

            _lastUsed = entries.ToDictionary(e => e.Id, e => e.LastUsed);
        }
        finally
        {
            _gate.Release();
        }
    }

    private sealed record Entry(Guid Id, DateTimeOffset LastUsed);

    private sealed record Document(int Version, IReadOnlyList<Entry?>? Entries);
}
