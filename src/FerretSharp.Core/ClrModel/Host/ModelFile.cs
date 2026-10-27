using FerretSharp.Core.IO;
using FerretSharp.Core.Resources;

namespace FerretSharp.Core.ClrModel;

/// <summary>Where the model in a model file comes from (WP-64): shown to the recipient instead of the build.</summary>
/// <param name="Project">Name of the exporter's project, without its path.</param>
/// <param name="Branch">Git branch of the project's working copy when exported; null outside one.</param>
/// <param name="Commit">The first seven characters of its commit; null outside a working copy.</param>
/// <param name="AppVersion">The FerretSharp that wrote the file.</param>
public sealed record ModelFileOrigin(
    string Project,
    string Configuration,
    string? ContextType,
    DateTimeOffset ExportedAt,
    string? Branch,
    string? Commit,
    string AppVersion);

/// <summary>A model file as read: the model and where it comes from, or why it could not be used.</summary>
public sealed record ModelFileRead(ModelExport? Model, ModelFileOrigin? Origin, string? Error);

/// <summary>
/// The C# model of a linked project as a file to pass on (WP-64, ADR 0037): whoever cannot build the project (packages
/// from a private feed) links the file instead – no build, no model host, no foreign code run. Unlike the cache the file
/// outlives FerretSharp versions: <see cref="CurrentVersion"/> is its own, changes to <see cref="ModelExport"/> stay additive
/// (new fields optional) and a newer FerretSharp reads every file of the same version. The file comes from someone else:
/// it is only data (no type names are resolved, nothing runs), bounded in size, and its names are checked before they can
/// reach generated code (<see cref="ModelFileCheck"/>).
/// </summary>
public static class ModelFile
{
    /// <summary>Version of the file; a change that is not additive raises it (older files then: "export again").</summary>
    public const int CurrentVersion = 1;

    /// <summary>Larger files are not read (a model of 425 entities is about 3 MB).</summary>
    public const long MaxBytes = 64L * 1024 * 1024;

    /// <summary>The name the save dialog suggests: <c>Shop.Data.ferretmodel.json</c>.</summary>
    public static string SuggestedName(ClrProjectLink link) => link.ProjectName + ".ferretmodel.json";

    /// <summary>Whether a path names a model file rather than a project (the connection dialog has one field for both).</summary>
    public static bool IsModelFile(string path) => path.EndsWith(".json", StringComparison.OrdinalIgnoreCase);

    /// <summary>The origin of a model exported from <paramref name="link"/> now: Git branch and commit read from its folder.</summary>
    public static ModelFileOrigin OriginOf(ClrProjectLink link, DateTimeOffset now, string appVersion)
    {
        var root = GitFolder.FindRoot(link.ProjectFile);
        return new ModelFileOrigin(link.ProjectName, link.Configuration, link.ContextType, now,
            root is null ? null : GitFolder.Branch(root), root is null ? null : GitFolder.Commit(root), appVersion);
    }

    /// <summary>
    /// Why this model cannot be passed on: a name the recipient would refuse (<see cref="ModelFileCheck"/>); null if it can.
    /// </summary>
    public static string? Problem(ModelExport model) => ModelFileCheck.Problem(model, null);

    /// <summary>Writes the file (atomically: a reader watching it never sees half of it).</summary>
    public static Task WriteAsync(string path, ModelExport model, ModelFileOrigin origin, CancellationToken cancellationToken) =>
        AtomicJsonFile.WriteAsync(path, new Document(CurrentVersion, origin, model), JsonFiles.Options, cancellationToken);

    /// <summary>Reads a model file; never throws for what is in or around the file – the reason is in the result.</summary>
    public static async Task<ModelFileRead> ReadAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            if (new FileInfo(path) is { Exists: true, Length: > MaxBytes })
            {
                return Failed(ClrModelText.ModelFileTooLarge, MaxBytes / (1024 * 1024));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return Failed(ClrModelText.ModelFileUnreadable, ex.Message);
        }

        var read = await VersionedJsonFile.ReadAsync<Document>(path, CurrentVersion, JsonFiles.Options, cancellationToken,
            complete: d => d is { Version: > 0, Origin: not null, Model: not null });
        return read switch
        {
            { State: JsonFileState.Ok, Document: { } document } when ModelFileCheck.Problem(document.Model, document.Origin) is { } problem =>
                new ModelFileRead(null, null, problem),
            { State: JsonFileState.Ok, Document: { } document } => new ModelFileRead(document.Model, document.Origin, null),
            { State: JsonFileState.Missing } => Failed(ClrModelText.ModelFileMissing, path),
            { State: JsonFileState.Newer, Version: var version } => Failed(ClrModelText.ModelFileNewer, version),
            { State: JsonFileState.Unreadable, Error: var error } => Failed(ClrModelText.ModelFileUnreadable, error?.Message),
            _ => Failed(ClrModelText.ModelFileNotAModel, Path.GetFileName(path)),
        };
    }

    private static ModelFileRead Failed(string format, object? argument) => new(null, null, TextFormat.Format(format, argument));

    private sealed record Document(int Version, ModelFileOrigin Origin, ModelExport Model);
}
