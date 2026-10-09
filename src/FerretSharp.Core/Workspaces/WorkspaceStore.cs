using System.Text.Json;
using FerretSharp.Core.Connections;
using FerretSharp.Core.IO;
using FerretSharp.Core.Resources;

namespace FerretSharp.Core.Workspaces;

/// <param name="Errors">Files that could not be read; they are skipped and left untouched.</param>
public sealed record WorkspaceLoadResult(IReadOnlyList<Workspace> Workspaces, IReadOnlyList<string> Errors);

public interface IWorkspaceStore
{
    Task<WorkspaceLoadResult> LoadAsync(Guid connectionId, CancellationToken cancellationToken);

    Task SaveAsync(Workspace workspace, CancellationToken cancellationToken);

    Task DeleteAsync(Guid workspaceId, CancellationToken cancellationToken);

    /// <summary>Removes all workspaces of a deleted connection.</summary>
    Task DeleteForConnectionAsync(Guid connectionId, CancellationToken cancellationToken);
}

/// <summary>One versioned JSON file per workspace (<c>workspaces/{id}.json</c>). Writes are atomic (temp file + move).</summary>
public sealed class WorkspaceStore(string directory) : IWorkspaceStore
{
    public const int CurrentVersion = 1;

    private readonly SemaphoreSlim _gate = new(1, 1);

    public string DirectoryPath { get; } = directory;

    public async Task<WorkspaceLoadResult> LoadAsync(Guid connectionId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var workspaces = new List<Workspace>();
            var errors = new List<string>();
            foreach (var (_, document, error) in await ReadAllAsync(cancellationToken))
            {
                if (error is not null)
                {
                    errors.Add(error);
                }
                else if (document!.Workspace.ConnectionId == connectionId)
                {
                    workspaces.Add(document.Workspace);
                }
            }

            return new WorkspaceLoadResult(workspaces, errors);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(Workspace workspace, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await AtomicJsonFile.WriteAsync(PathOf(workspace.Id), new WorkspaceDocument(CurrentVersion, workspace), JsonFiles.Options, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeleteAsync(Guid workspaceId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            File.Delete(PathOf(workspaceId));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeleteForConnectionAsync(Guid connectionId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            foreach (var (file, document, _) in await ReadAllAsync(cancellationToken))
            {
                if (document?.Workspace.ConnectionId == connectionId)
                {
                    File.Delete(file);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private string PathOf(Guid workspaceId) => Path.Combine(DirectoryPath, workspaceId.ToString("D") + ".json");

    private async Task<List<(string File, WorkspaceDocument? Document, string? Error)>> ReadAllAsync(CancellationToken cancellationToken)
    {
        var result = new List<(string, WorkspaceDocument?, string?)>();
        if (!Directory.Exists(DirectoryPath))
        {
            return result;
        }

        foreach (var file in Directory.EnumerateFiles(DirectoryPath, "*.json"))
        {
            try
            {
                await using var stream = File.OpenRead(file);
                var document = await JsonSerializer.DeserializeAsync<WorkspaceDocument>(stream, JsonFiles.Options, cancellationToken);
                if (document?.Workspace is null)
                {
                    result.Add((file, null, TextFormat.Format(WorkspaceText.FileEmpty, Path.GetFileName(file))));
                }
                else if (document.Version > CurrentVersion)
                {
                    result.Add((file, null, TextFormat.Format(WorkspaceText.FileFromNewerVersion, Path.GetFileName(file), document.Version)));
                }
                else
                {
                    result.Add((file, document, null));
                }
            }
            catch (JsonException ex)
            {
                result.Add((file, null, TextFormat.Format(WorkspaceText.InvalidFile, Path.GetFileName(file), ex.Message)));
            }
            catch (IOException ex)
            {
                result.Add((file, null, $"{Path.GetFileName(file)}: {ex.Message}"));
            }
        }

        return result;
    }

    private sealed record WorkspaceDocument(int Version, Workspace Workspace);
}
