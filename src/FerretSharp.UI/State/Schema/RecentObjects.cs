using FerretSharp.Core.Workspaces;
using Microsoft.Extensions.Logging;

namespace FerretSharp.UI.State;

/// <summary>
/// The tables, views and PL/SQL units last opened, per connection (WP-91): every tab of one that becomes active counts –
/// from the explorer, the quick open, a FK jump or a link –, a time travel tab does not (it shows another point in time).
/// Read from the connection's file on first use; each visit is shown at once and written in the order it came – the list
/// in memory is what counts, a file that takes no entry does not shorten it. Used from
/// the UI thread only.
/// </summary>
public sealed class RecentObjects(RecentObjectStore store, ILogger<RecentObjects> logger, TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Dictionary<Guid, IReadOnlyList<RecentObject>> _byConnection = [];

    /// <summary>The last object tracked per connection: a tab shown again and again (every render) is written once.</summary>
    private readonly Dictionary<Guid, RecentObject> _last = [];

    /// <summary>Reads and writes one after the other: a read that ends after a write would bring back the older list.</summary>
    private Task _queue = Task.CompletedTask;

    /// <summary>Raised when a connection's list was read or changed.</summary>
    public event Action? Changed;

    /// <summary>The connection's objects, newest first; empty until its file was read (the first call starts reading).</summary>
    public IReadOnlyList<RecentObject> For(Guid connectionId)
    {
        if (_byConnection.TryGetValue(connectionId, out var list))
        {
            return list;
        }

        _byConnection[connectionId] = [];
        Enqueue(async () => Use(connectionId, await store.LoadAsync(connectionId, CancellationToken.None)));
        return [];
    }

    /// <summary>The active tab of a connection: a table or PL/SQL tab counts as opened now.</summary>
    public void Track(Guid? connectionId, WorkspaceTab? tab)
    {
        if (connectionId is not { } id || EntryOf(tab) is not { } entry || _last.TryGetValue(id, out var last) && last.Same(entry))
        {
            return;
        }

        _last[id] = entry;
        var list = For(id); // starts reading the file on first use; what it holds comes behind this entry (Use)
        _byConnection[id] = [entry, .. list.Where(e => !e.Same(entry)).Take(RecentObjectStore.MaxEntries - 1)];
        Changed?.Invoke();
        // The list in memory stays as it is (review WP-91, 2): a file that takes no entry (of a newer FerretSharp, locked for
        // the moment) answers with this entry alone, and the visits before were gone from the quick open.
        Enqueue(() => store.AddAsync(id, entry, CancellationToken.None));
    }

    /// <summary>What a tab counts as; null for SQL, LINQ and other tabs, and for a time travel tab.</summary>
    private RecentObject? EntryOf(WorkspaceTab? tab) => tab switch
    {
        TableTab { TimeTravel: null } table => RecentObject.Of(table.Table, _time.GetUtcNow()),
        PlSqlTab unit => RecentObject.Of(unit.Unit, _time.GetUtcNow()),
        _ => null,
    };

    /// <summary>
    /// The connection's file was read: the visits since (shown already, newest first) stay in front of what it holds – a read
    /// can end after the first visits of the connection.
    /// </summary>
    private void Use(Guid connectionId, IReadOnlyList<RecentObject> read)
    {
        var shown = _byConnection.GetValueOrDefault(connectionId) ?? [];
        _byConnection[connectionId] = [.. shown, .. read.Where(r => !shown.Any(r.Same))
            .Take(Math.Max(0, RecentObjectStore.MaxEntries - shown.Count))];
        Changed?.Invoke();
    }

    /// <summary>Runs <paramref name="work"/> after what was queued before; a file that cannot be read or written only leaves the list in memory.</summary>
    private void Enqueue(Func<Task> work) => _queue = RunAfterAsync(_queue, work);

    private async Task RunAfterAsync(Task before, Func<Task> work)
    {
        await before;
        try
        {
            await work();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "The recently opened objects could not be read or written in {Directory}", store.Directory);
        }
    }

    /// <summary>Waits for what is queued (tests).</summary>
    internal Task IdleAsync() => _queue;
}
