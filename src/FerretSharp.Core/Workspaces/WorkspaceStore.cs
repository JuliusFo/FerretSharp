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
            try
            {
                await using var stream = File.OpenRead(file);
                var document = await JsonSerializer.DeserializeAsync<WorkspaceDocument>(stream, JsonFiles.Options, cancellationToken);
                if (document?.Workspace is null)
                {
                    result.Add((file, null, TextFormat.Format(WorkspaceText.FileEmpty, Path.GetFileName(file)), null));
                }
                else if (document.Version > CurrentVersion)
                {
                    result.Add((file, null, TextFormat.Format(WorkspaceText.FileFromNewerVersion, Path.GetFileName(file), document.Version),
                        document.Workspace.ConnectionId));
                }
                else
                {
                    result.Add((file, document, null, document.Workspace.ConnectionId));
                }
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException)
            {
                result.Add((file, null, TextFormat.Format(WorkspaceText.InvalidFile, Path.GetFileName(file), ex.Message), await OwnerOfAsync(file)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // One file that cannot be read (rights, locked) does not stop the others (A41).
                result.Add((file, null, $"{Path.GetFileName(file)}: {ex.Message}", null));
            }
        }

        return result;
    }

    /// <summary>The connection a broken file names, if its text still shows it.</summary>
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
