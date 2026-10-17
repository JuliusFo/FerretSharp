using System.Collections.Concurrent;
using FerretSharp.Core.Workspaces;

namespace FerretSharp.Testing;

/// <summary>Workspace files in memory. Thread-safe: the debounced saver writes from a timer thread while a test reads.</summary>
public sealed class InMemoryWorkspaceStore : IWorkspaceStore
{
    private int _saveCount;
    private Exception? _failNextSave;

    public ConcurrentDictionary<Guid, Workspace> Saved { get; } = [];

    public List<string> LoadErrors { get; } = [];

    /// <summary>Thrown by the next save, then reset.</summary>
    public Exception? FailNextSave
    {
        get => Volatile.Read(ref _failNextSave);
        set => Volatile.Write(ref _failNextSave, value);
    }

    public int SaveCount => Volatile.Read(ref _saveCount);

    public Task<WorkspaceLoadResult> LoadAsync(Guid connectionId, CancellationToken cancellationToken) =>
        Task.FromResult(new WorkspaceLoadResult(Saved.Values.Where(w => w.ConnectionId == connectionId).ToList(), LoadErrors.ToList()));

    public Task SaveAsync(Workspace workspace, CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _failNextSave, null) is { } failure)
        {
            return Task.FromException(failure);
        }

        Interlocked.Increment(ref _saveCount);
        Saved[workspace.Id] = workspace;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(Guid workspaceId, CancellationToken cancellationToken)
    {
        Saved.TryRemove(workspaceId, out _);
        return Task.CompletedTask;
    }

    public Task DeleteForConnectionAsync(Guid connectionId, CancellationToken cancellationToken)
    {
        foreach (var id in Saved.Values.Where(w => w.ConnectionId == connectionId).Select(w => w.Id).ToList())
        {
            Saved.TryRemove(id, out _);
        }

        return Task.CompletedTask;
    }
}
