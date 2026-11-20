using FerretSharp.Core.IO;
using FerretSharp.Core.Resources;

namespace FerretSharp.Core.Schema;

/// <summary>
/// What kind of object the explorer lists (WP-91): a table, view or materialized view – one namespace in Oracle – or a
/// PL/SQL unit by its kind (a trigger has a namespace of its own).
/// </summary>
public enum SchemaObjectKind
{
    Table,
    Package,
    Procedure,
    Function,
    Trigger,
}

/// <summary>
/// An object marked as favorite (WP-91), by kind and the name the explorer shows (<see cref="TableSummary.DisplayName"/>,
/// the synonym if there is one) – without owner, like the filter presets (WP-37): the schema is the same on Dev, Test
/// and Prod, its owner's name may differ, so a favorite holds on every connection (decision of the user 2026-11-20).
/// </summary>
/// <param name="Name">Compared ordinally, like Oracle names.</param>
public sealed record Favorite(SchemaObjectKind Kind, string Name)
{
    public static Favorite Of(TableSummary table) => new(SchemaObjectKind.Table, table.DisplayName);

    public static Favorite Of(PlSqlObjectSummary unit) => new(SchemaObjects.KindOf(unit.Kind), unit.DisplayName);
}

/// <summary>Kinds of the objects the explorer lists.</summary>
public static class SchemaObjects
{
    public static SchemaObjectKind KindOf(PlSqlKind kind) => kind switch
    {
        PlSqlKind.Package => SchemaObjectKind.Package,
        PlSqlKind.Procedure => SchemaObjectKind.Procedure,
        PlSqlKind.Function => SchemaObjectKind.Function,
        _ => SchemaObjectKind.Trigger,
    };

    /// <summary>The PL/SQL kind of <paramref name="kind"/>; null for a table.</summary>
    public static PlSqlKind? PlSqlKindOf(SchemaObjectKind kind) => kind switch
    {
        SchemaObjectKind.Package => PlSqlKind.Package,
        SchemaObjectKind.Procedure => PlSqlKind.Procedure,
        SchemaObjectKind.Function => PlSqlKind.Function,
        SchemaObjectKind.Trigger => PlSqlKind.Trigger,
        _ => null,
    };
}

/// <summary>
/// The favorites of all connections in one file (<c>favorites.json</c>, WP-91). The user sets them one by one, so a file
/// that is broken, unreadable or of a newer FerretSharp is reported and never overwritten – as the filter presets.
/// </summary>
public sealed class FavoriteStore(string filePath)
{
    private const int CurrentVersion = 1;

    /// <summary>More would be no list to pick from; a file edited by hand keeps the first ones.</summary>
    public const int MaxEntries = 500;

    /// <summary>Longer than any Oracle name (128 bytes) in quotes: a file edited by hand cannot make the list huge.</summary>
    private const int MaxNameLength = 260;

    private readonly SemaphoreSlim _gate = new(1, 1);

    public string FilePath => filePath;

    /// <exception cref="IOException">The file is broken, unreadable or of a newer version.</exception>
    public async Task<IReadOnlyList<Favorite>> LoadAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var read = await ReadAsync(cancellationToken);
            return read.State switch
            {
                JsonFileState.Missing or JsonFileState.Empty => [],
                JsonFileState.Ok => read.Document!.Favorites!.OfType<Favorite>().ToList(),
                _ => throw Problem(read),
            };
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <exception cref="IOException">The file on disk is broken or of a newer version (it is kept), or cannot be written.</exception>
    public async Task SaveAsync(IReadOnlyList<Favorite> favorites, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(favorites);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var read = await ReadAsync(cancellationToken);
            if (read.State is JsonFileState.Broken or JsonFileState.Newer)
            {
                throw Problem(read, saving: true);
            }

            await AtomicJsonFile.WriteAsync(filePath, new Document(CurrentVersion, favorites), JsonFiles.Options, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private Task<JsonFileRead<Document>> ReadAsync(CancellationToken cancellationToken) =>
        VersionedJsonFile.ReadAsync<Document>(filePath, CurrentVersion, JsonFiles.Options, cancellationToken, normalize: Normalize);

    /// <summary>Entries edited by hand: without a name, of an unknown kind or twice are left out; null lists become empty.</summary>
    private static Document Normalize(Document document) => document with
    {
        Favorites = (document.Favorites ?? [])
            .OfType<Favorite>()
            .Where(f => !string.IsNullOrWhiteSpace(f.Name) && f.Name.Length <= MaxNameLength && Enum.IsDefined(f.Kind))
            .Distinct()
            .Take(MaxEntries)
            .ToList(),
    };

    private IOException Problem(JsonFileRead<Document> read, bool saving = false) => read.State switch
    {
        JsonFileState.Newer => new IOException(TextFormat.Format(saving ? CoreText.FileFromNewerVersionNotOverwritten : CoreText.FileFromNewerVersion, filePath, read.Version)),
        JsonFileState.Broken => new IOException(TextFormat.Format(saving ? WorkspaceText.FavoritesFileInvalidNotOverwritten : WorkspaceText.FavoritesFileInvalid, filePath, read.Error!.Message), read.Error),
        _ => new IOException(TextFormat.Format(WorkspaceText.FavoritesFileUnreadable, filePath, read.Error?.Message), read.Error),
    };

    private sealed record Document(int Version, IReadOnlyList<Favorite?>? Favorites);
}
