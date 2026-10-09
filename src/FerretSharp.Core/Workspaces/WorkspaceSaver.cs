using FerretSharp.Core.Resources;

namespace FerretSharp.Core.Workspaces;

/// <summary>
/// Saving the workspaces (R2, out of <see cref="WorkspaceManager"/>): which ones changed, a debounced save shortly after a
/// change (<see cref="WorkspaceManager.SaveDelay"/>), an immediate one on request. A failed save keeps the changes for
/// the next attempt and is reported (<see cref="Error"/>). Thread-safe.
/// </summary>
/// <param name="current">The current state of the given workspaces (the manager reads them under its lock).</param>
/// <param name="errorChanged">Raised when <see cref="Error"/> changed.</param>
internal sealed class WorkspaceSaver(
    IWorkspaceStore store, TimeProvider time, Func<IReadOnlyCollection<Guid>, IReadOnlyList<Workspace>> current, Action errorChanged)
{
    private readonly Lock _lock = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HashSet<Guid> _dirty = [];
    private ITimer? _timer;

    /// <summary>Set while a workspace could not be saved; cleared after the next successful save.</summary>
    public string? Error { get; private set; }

    public void MarkDirty(Guid workspaceId)
    {
        lock (_lock)
        {
            _dirty.Add(workspaceId);
        }
    }

    /// <summary>Saves the dirty workspaces after <see cref="WorkspaceManager.SaveDelay"/>; changes until then go along.</summary>
    public void Schedule()
    {
        lock (_lock)
        {
            _timer ??= time.CreateTimer(_ => _ = FlushAsync(), null, WorkspaceManager.SaveDelay, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>The workspace is gone (deleted): nothing to save of it any more.</summary>
    public void Forget(Guid workspaceId)
    {
        lock (_lock)
        {
            _dirty.Remove(workspaceId);
        }
    }

    /// <summary>Disconnected: no timer, nothing dirty (the caller flushed before).</summary>
    public void Reset()
    {
        lock (_lock)
        {
            _timer?.Dispose();
            _timer = null;
            _dirty.Clear();
        }
    }

    /// <summary>Writes all dirty workspaces now. Never throws: a failure stays dirty and sets <see cref="Error"/>.</summary>
    public async Task FlushAsync()
    {
        await _gate.WaitAsync();
        try
        {
            List<Guid> ids;
            lock (_lock)
            {
                _timer?.Dispose();
                _timer = null;
                ids = [.. _dirty];
                _dirty.Clear();
            }

            string? error = null;
            foreach (var workspace in current(ids))
            {
                try
                {
                    await store.SaveAsync(workspace, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    // Any failure (locked file, a value that does not serialize …): keep the changes for the next save and say so.
                    MarkDirty(workspace.Id);
                    error = TextFormat.Format(WorkspaceText.SaveFailed, workspace.Name, ex.Message);
                }
            }

            if (Error != error)
            {
                Error = error;
                errorChanged();
            }
        }
        finally
        {
            _gate.Release();
        }
    }
}
