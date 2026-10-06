using System.Text.Json;
using FerretSharp.Core.IO;

namespace FerretSharp.Core.Connections;

/// <summary>When each connection was last used; kept separate from connections.json so connecting never rewrites profiles.</summary>
public sealed class RecentConnections(string filePath, TimeProvider? timeProvider = null)
{
    public const int MaxEntries = 100;

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Dictionary<Guid, DateTimeOffset> _lastUsed = [];

    public IReadOnlyDictionary<Guid, DateTimeOffset> LastUsed => _lastUsed;

    /// <summary>Most recently used first, limited to ids that still exist.</summary>
    public IReadOnlyList<ConnectionProfile> MostRecent(IEnumerable<ConnectionProfile> profiles, int count) =>
        profiles
            .Where(p => _lastUsed.ContainsKey(p.Id))
            .OrderByDescending(p => _lastUsed[p.Id])
            .Take(count)
            .ToList();

    /// <summary>A broken or missing file just means "no history".</summary>
    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(filePath))
            {
                return;
            }

            await using var stream = File.OpenRead(filePath);
            var document = await JsonSerializer.DeserializeAsync<Document>(stream, ConnectionStore.JsonOptions, cancellationToken);
            _lastUsed = document?.Entries.ToDictionary(e => e.Id, e => e.LastUsed) ?? [];
        }
        catch (JsonException)
        {
            _lastUsed = [];
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

            await AtomicJsonFile.WriteAsync(filePath, new Document(1, entries), ConnectionStore.JsonOptions, cancellationToken);
            _lastUsed = entries.ToDictionary(e => e.Id, e => e.LastUsed);
        }
        finally
        {
            _gate.Release();
        }
    }

    private sealed record Entry(Guid Id, DateTimeOffset LastUsed);

    private sealed record Document(int Version, IReadOnlyList<Entry> Entries);
}
