using FerretSharp.Core.Workspaces;

namespace FerretSharp.Core.Tests.Fakes;

public sealed class InMemoryWorkspaceStore : IWorkspaceStore
{
    public Dictionary<Guid, Workspace> Saved { get; } = [];

    public List<string> LoadErrors { get; } = [];

    /// <summary>Thrown by the next save, then reset.</summary>
    public Exception? FailNextSave { get; set; }

    public int SaveCount { get; private set; }

    public Task<WorkspaceLoadResult> LoadAsync(Guid connectionId, CancellationToken cancellationToken) =>
        Task.FromResult(new WorkspaceLoadResult(Saved.Values.Where(w => w.ConnectionId == connectionId).ToList(), LoadErrors.ToList()));

    public Task SaveAsync(Workspace workspace, CancellationToken cancellationToken)
    {
        if (FailNextSave is { } failure)
        {
            FailNextSave = null;
            return Task.FromException(failure);
        }

        SaveCount++;
        Saved[workspace.Id] = workspace;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(Guid workspaceId, CancellationToken cancellationToken)
    {
        Saved.Remove(workspaceId);
        return Task.CompletedTask;
    }

    public Task DeleteForConnectionAsync(Guid connectionId, CancellationToken cancellationToken)
    {
        foreach (var id in Saved.Values.Where(w => w.ConnectionId == connectionId).Select(w => w.Id).ToList())
        {
            Saved.Remove(id);
        }

        return Task.CompletedTask;
    }
}
