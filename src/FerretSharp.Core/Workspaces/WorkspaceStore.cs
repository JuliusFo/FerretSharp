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
public sealed partial class WorkspaceStore(string directory) : IWorkspaceStore
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
            foreach (var (_, document, error, owner) in await ReadAllAsync(cancellationToken))
            {
                if (error is not null)
                {
                    // Only this connection's files, and those whose connection is unknown (review 3.31.0, A41: a broken
                    // file of one connection was reported on connecting to every other one).
                    if (owner is null || owner == connectionId)
                    {
                        errors.Add(error);
                    }
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
            foreach (var (file, document, _, _) in await ReadAllAsync(cancellationToken))
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

    /// <returns>Per file its document or why it cannot be read, and the connection it belongs to as far as known.</returns>
    private async Task<List<(string File, WorkspaceDocument? Document, string? Error, Guid? Owner)>> ReadAllAsync(CancellationToken cancellationToken)
    {
        var result = new List<(string, WorkspaceDocument?, string?, Guid?)>();
        if (!Directory.Exists(DirectoryPath))
        {
            return result;
        }

        foreach (var file in Directory.EnumerateFiles(DirectoryPath, "*.json"))
        {
            var name = Path.GetFileName(file);
            var read = await VersionedJsonFile.ReadAsync<WorkspaceDocument>(file, CurrentVersion, JsonFiles.Options, cancellationToken,
                complete: document => document.Workspace is not null,
                normalize: document => document with { Workspace = document.Workspace.Normalized() });
            if (read.State == JsonFileState.Missing)
            {
                continue; // deleted meanwhile
            }

            // Every file on its own: one that cannot be used is reported and left as it is, the others still load (A41).
            result.Add(read.State switch
            {
                JsonFileState.Ok => (file, read.Document, null, read.Document!.Workspace.ConnectionId),
                JsonFileState.Empty => (file, null, TextFormat.Format(CoreText.FileEmpty, name), null),
                JsonFileState.Newer => (file, null, TextFormat.Format(CoreText.FileFromNewerVersion, name, read.Version), await OwnerOfAsync(file)),
                JsonFileState.Broken => (file, null, TextFormat.Format(WorkspaceText.InvalidFile, name, read.Error!.Message), await OwnerOfAsync(file)),
                _ => (file, null, $"{name}: {read.Error!.Message}", null), // unreadable: rights, locked
            });
        }

        return result;
    }

    /// <summary>The connection a broken or newer file names, if its text still shows it.</summary>
    private static async Task<Guid?> OwnerOfAsync(string file)
    {
        try
        {
            return ConnectionIdText().Match(await File.ReadAllTextAsync(file)) is { Success: true } match
                   && Guid.TryParse(match.Groups["id"].Value, out var id)
                ? id
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"""connectionId""\s*:\s*""(?<id>[0-9a-fA-F-]{36})""")]
    private static partial System.Text.RegularExpressions.Regex ConnectionIdText();

    private sealed record WorkspaceDocument(int Version, Workspace Workspace);
}
