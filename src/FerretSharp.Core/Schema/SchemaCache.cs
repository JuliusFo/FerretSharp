using System.Collections.Concurrent;

namespace FerretSharp.Core.Schema;

/// <summary>
/// Schema metadata for one connection: object list (own objects plus synonym targets) and foreign keys are loaded
/// up front, column details lazily per table (ALL_TAB_COLUMNS is slow on large databases). <see cref="RefreshAsync"/>
/// drops everything.
/// </summary>
public sealed class SchemaCache(ISchemaReader reader, string owner)
{
    private ConcurrentDictionary<TableRef, Lazy<Task<TableDetails>>> _details = new();
    private Dictionary<TableRef, TableSummary> _byRef = [];
    private ILookup<TableRef, ForeignKeyInfo> _outgoing = Array.Empty<ForeignKeyInfo>().ToLookup(f => f.From);
    private ILookup<TableRef, ForeignKeyInfo> _incoming = Array.Empty<ForeignKeyInfo>().ToLookup(f => f.To);

    public string Owner { get; } = owner;

    /// <summary>Own objects and synonym targets, one entry per real object, sorted by display name.</summary>
    public IReadOnlyList<TableSummary> Tables { get; private set; } = [];

    public IReadOnlyList<ForeignKeyInfo> ForeignKeys { get; private set; } = [];

    public DateTimeOffset? LoadedAt { get; private set; }

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        var own = await reader.GetTablesAsync(Owner, cancellationToken);
        var synonymTargets = await reader.GetSynonymTargetsAsync(Owner, cancellationToken);
        var tables = Merge(own, synonymTargets);

        // Foreign keys of every schema that contributes objects, so relationships work across synonyms too.
        var foreignKeys = new List<ForeignKeyInfo>();
        foreach (var schema in tables.Select(t => t.Owner).Prepend(Owner).Distinct(StringComparer.Ordinal))
        {
            foreignKeys.AddRange(await reader.GetForeignKeysAsync(schema, cancellationToken));
        }

        Tables = tables;
        ForeignKeys = foreignKeys;
        _byRef = tables.ToDictionary(t => t.Ref);
        _outgoing = foreignKeys.ToLookup(f => f.From);
        _incoming = foreignKeys.ToLookup(f => f.To);
        _details = new ConcurrentDictionary<TableRef, Lazy<Task<TableDetails>>>();
        LoadedAt = DateTimeOffset.Now;
    }

    public Task RefreshAsync(CancellationToken cancellationToken) => LoadAsync(cancellationToken);

    public TableSummary? Find(TableRef table) => _byRef.GetValueOrDefault(table);

    /// <summary>Loads details once per table; a failed or cancelled load is not cached.</summary>
    public async Task<TableDetails> GetDetailsAsync(TableSummary table, CancellationToken cancellationToken)
    {
        var details = _details;
        var entry = details.GetOrAdd(table.Ref, _ => new Lazy<Task<TableDetails>>(() => reader.GetDetailsAsync(table, cancellationToken)));
        try
        {
            return await entry.Value;
        }
        catch
        {
            details.TryRemove(new KeyValuePair<TableRef, Lazy<Task<TableDetails>>>(table.Ref, entry));
            throw;
        }
    }

    // Detail views (not cached here: each tab keeps what it loaded until it is reloaded).

    public Task<ObjectInfo> GetObjectInfoAsync(TableSummary table, CancellationToken cancellationToken) =>
        reader.GetObjectInfoAsync(table, cancellationToken);

    public Task<IReadOnlyList<ConstraintInfo>> GetConstraintsAsync(TableRef table, CancellationToken cancellationToken) =>
        reader.GetConstraintsAsync(table, cancellationToken);

    public Task<IReadOnlyList<IndexInfo>> GetIndexesAsync(TableRef table, CancellationToken cancellationToken) =>
        reader.GetIndexesAsync(table, cancellationToken);

    public Task<ObjectDependencies> GetDependenciesAsync(TableSummary table, CancellationToken cancellationToken) =>
        reader.GetDependenciesAsync(table, cancellationToken);

    public Task<string> GetDdlAsync(TableSummary table, CancellationToken cancellationToken) =>
        reader.GetDdlAsync(table, cancellationToken);

    public Task<IReadOnlyList<Data.LockHolder>?> GetLockHoldersAsync(TableRef table, CancellationToken cancellationToken) =>
        reader.GetLockHoldersAsync(table, cancellationToken);

    /// <summary>Column names of all objects of a schema in one query (not cached: the C# model comparison reads them once).</summary>
    public Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> GetColumnNamesAsync(string owner, CancellationToken cancellationToken) =>
        reader.GetColumnNamesAsync(owner, cancellationToken);

    /// <summary>Relationships where <paramref name="table"/> references other tables.</summary>
    public IEnumerable<ForeignKeyInfo> OutgoingOf(TableRef table) => _outgoing[table];

    /// <summary>Relationships where other tables reference <paramref name="table"/>.</summary>
    public IEnumerable<ForeignKeyInfo> IncomingOf(TableRef table) => _incoming[table];

    /// <summary>
    /// Own objects win over synonyms to them; several synonyms for the same object collapse into one entry,
    /// preferring a private synonym over a public one.
    /// </summary>
    internal static IReadOnlyList<TableSummary> Merge(IReadOnlyList<TableSummary> own, IReadOnlyList<TableSummary> synonymTargets)
    {
        var ownRefs = own.Select(t => t.Ref).ToHashSet();
        var viaSynonym = synonymTargets
            .Where(t => !ownRefs.Contains(t.Ref))
            .GroupBy(t => t.Ref)
            .Select(g => g.OrderBy(t => t.Synonym?.IsPublic ?? false).ThenBy(t => t.DisplayName, StringComparer.Ordinal).First());

        return own.Concat(viaSynonym)
            .OrderBy(t => t.DisplayName, StringComparer.Ordinal)
            .ThenBy(t => t.Owner, StringComparer.Ordinal)
            .ToList();
    }
}
