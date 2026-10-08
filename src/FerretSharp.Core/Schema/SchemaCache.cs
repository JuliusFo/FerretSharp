using System.Collections.Concurrent;

namespace FerretSharp.Core.Schema;

/// <summary>
/// Schema metadata for one connection: object list (own objects plus synonym targets) and foreign keys are loaded
/// up front, column details lazily per table (ALL_TAB_COLUMNS is slow on large databases). <see cref="RefreshAsync"/>
/// drops everything except relationships of other sources (<see cref="SetForeignKeys"/>).
/// </summary>
public sealed class SchemaCache(ISchemaReader reader, string owner)
{
    private readonly Lock _foreignKeyLock = new();
    private readonly Dictionary<FkSource, IReadOnlyList<ForeignKeyInfo>> _otherSources = [];
    private IReadOnlyList<ForeignKeyInfo> _declared = [];
    private ConcurrentDictionary<TableRef, Lazy<Task<TableDetails>>> _details = new();
    private Dictionary<TableRef, TableSummary> _byRef = [];
    private Dictionary<PlSqlRef, PlSqlObjectSummary> _plSqlByRef = [];
    private ILookup<TableRef, ForeignKeyInfo> _outgoing = Array.Empty<ForeignKeyInfo>().ToLookup(f => f.From);
    private ILookup<TableRef, ForeignKeyInfo> _incoming = Array.Empty<ForeignKeyInfo>().ToLookup(f => f.To);

    public string Owner { get; } = owner;

    /// <summary>Own objects and synonym targets, one entry per real object, sorted by display name.</summary>
    public IReadOnlyList<TableSummary> Tables { get; private set; } = [];

    /// <summary>Own packages, procedures, functions and triggers plus synonym targets, one entry per unit, sorted by display name (WP-28).</summary>
    public IReadOnlyList<PlSqlObjectSummary> PlSqlObjects { get; private set; } = [];

    /// <summary>Declared foreign keys, then relationships of other sources that no declared one covers.</summary>
    public IReadOnlyList<ForeignKeyInfo> ForeignKeys { get; private set; } = [];

    /// <summary>
    /// Replaces the relationships of a source other than <see cref="FkSource.Declared"/> (the C# model's navigations).
    /// One that a declared foreign key already covers (same tables, same column pairs) is left out; this is checked
    /// again whenever the schema is reloaded.
    /// </summary>
    public void SetForeignKeys(FkSource source, IReadOnlyList<ForeignKeyInfo> foreignKeys)
    {
        if (source == FkSource.Declared)
        {
            throw new ArgumentException("Declared foreign keys come from the database.", nameof(source));
        }

        lock (_foreignKeyLock)
        {
            _otherSources[source] = foreignKeys;
            CombineForeignKeys();
        }
    }

    public DateTimeOffset? LoadedAt { get; private set; }

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        var own = await reader.GetTablesAsync(Owner, cancellationToken);
        var synonymTargets = await reader.GetSynonymTargetsAsync(Owner, cancellationToken);
        var tables = Merge(own, synonymTargets.Tables);
        var plSql = Merge(await reader.GetPlSqlObjectsAsync(Owner, cancellationToken), synonymTargets.PlSql);

        // Foreign keys of every schema that contributes objects, so relationships work across synonyms too.
        var foreignKeys = new List<ForeignKeyInfo>();
        foreach (var schema in tables.Select(t => t.Owner).Prepend(Owner).Distinct(StringComparer.Ordinal))
        {
            foreignKeys.AddRange(await reader.GetForeignKeysAsync(schema, cancellationToken));
        }

        Tables = tables;
        _byRef = tables.ToDictionary(t => t.Ref);
        PlSqlObjects = plSql;
        _plSqlByRef = plSql.ToDictionary(o => o.Ref);
        lock (_foreignKeyLock)
        {
            _declared = foreignKeys;
            CombineForeignKeys();
        }

        _details = new ConcurrentDictionary<TableRef, Lazy<Task<TableDetails>>>();
        LoadedAt = DateTimeOffset.Now;
    }

    private void CombineForeignKeys()
    {
        var declared = _declared.Select(PairsOf).ToHashSet();
        var all = _declared.ToList();
        foreach (var (_, foreignKeys) in _otherSources.OrderBy(s => s.Key))
        {
            foreach (var fk in foreignKeys)
            {
                if (declared.Add(PairsOf(fk)))
                {
                    all.Add(fk);
                }
            }
        }

        ForeignKeys = all;
        _outgoing = all.ToLookup(f => f.From);
        _incoming = all.ToLookup(f => f.To);
    }

    /// <summary>A relationship by what it connects: both tables and the column pairs in a fixed order.</summary>
    private static string PairsOf(ForeignKeyInfo fk) =>
        $"{fk.From}\0{fk.To}\0" + string.Join('\0', fk.FromColumns.Zip(fk.ToColumns, (from, to) => from + "\u0001" + to).Order(StringComparer.Ordinal));

    public Task RefreshAsync(CancellationToken cancellationToken) => LoadAsync(cancellationToken);

    public TableSummary? Find(TableRef table) => _byRef.GetValueOrDefault(table);

    public PlSqlObjectSummary? FindPlSql(PlSqlRef unit) => _plSqlByRef.GetValueOrDefault(unit);

    /// <summary>The PL/SQL unit behind an object of the dependency list (a package body leads to its package); null for other objects.</summary>
    public PlSqlObjectSummary? FindPlSql(string owner, string name, string objectType) =>
        PlSqlKinds.Of(objectType) is { } kind ? FindPlSql(new PlSqlRef(owner, name, kind.Kind)) : null;

    /// <summary>
    /// Loads details once per table; a failed load is not cached. Callers share the load, so it does not run with any
    /// one caller's token: a cancelled caller (closed tab, abandoned completion) stops waiting, the others get the result.
    /// </summary>
    public async Task<TableDetails> GetDetailsAsync(TableSummary table, CancellationToken cancellationToken)
    {
        var details = _details;
        var entry = details.GetOrAdd(table.Ref, _ => new Lazy<Task<TableDetails>>(() => ReadDetailsAsync(table)));
        var load = entry.Value;
        try
        {
            return await load.WaitAsync(cancellationToken);
        }
        catch when (load.IsFaulted || load.IsCanceled)
        {
            details.TryRemove(new KeyValuePair<TableRef, Lazy<Task<TableDetails>>>(table.Ref, entry));
            throw;
        }
    }

    /// <summary>Async, so even a reader that throws at once ends up in the task (the Lazy must not cache an exception).</summary>
    private async Task<TableDetails> ReadDetailsAsync(TableSummary table) => await reader.GetDetailsAsync(table, CancellationToken.None);

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

    // PL/SQL views (WP-28), not cached either.

    public Task<PlSqlObjectInfo> GetPlSqlInfoAsync(PlSqlObjectSummary unit, CancellationToken cancellationToken) =>
        reader.GetPlSqlInfoAsync(unit, cancellationToken);

    public Task<PlSqlSource> GetSourceAsync(PlSqlObjectSummary unit, PlSqlPart part, CancellationToken cancellationToken) =>
        reader.GetSourceAsync(unit, part, cancellationToken);

    public Task<IReadOnlyList<PlSqlSubprogram>> GetSubprogramsAsync(PlSqlObjectSummary unit, CancellationToken cancellationToken) =>
        reader.GetSubprogramsAsync(unit, cancellationToken);

    public Task<IReadOnlyList<PlSqlError>> GetErrorsAsync(PlSqlObjectSummary unit, CancellationToken cancellationToken) =>
        reader.GetErrorsAsync(unit, cancellationToken);

    public Task<ObjectDependencies> GetDependenciesAsync(PlSqlObjectSummary unit, CancellationToken cancellationToken) =>
        reader.GetDependenciesAsync(unit, cancellationToken);

    /// <summary>Lines of the schema's own PL/SQL containing the text (owner filter: synonym targets are not searched).</summary>
    public Task<IReadOnlyList<SourceHit>> SearchSourceAsync(string text, int limit, CancellationToken cancellationToken) =>
        reader.SearchSourceAsync(Owner, text, limit, cancellationToken);

    public Task<IReadOnlyList<Data.LockHolder>?> GetLockHoldersAsync(TableRef table, CancellationToken cancellationToken) =>
        reader.GetLockHoldersAsync(table, cancellationToken);

    /// <summary>Columns of all objects of a schema in one query (not cached: the C# model comparison reads them once).</summary>
    public Task<IReadOnlyDictionary<string, IReadOnlyList<ColumnInfo>>> GetColumnsAsync(string owner, CancellationToken cancellationToken) =>
        reader.GetColumnsAsync(owner, cancellationToken);

    /// <summary>The estimated plan of a query (explorer session, ADR 0012).</summary>
    public Task<Query.ExecutionPlan> ExplainAsync(Query.QuerySpec query, CancellationToken cancellationToken) =>
        reader.ExplainAsync(query, cancellationToken);

    /// <summary>Relationships where <paramref name="table"/> references other tables.</summary>
    public IEnumerable<ForeignKeyInfo> OutgoingOf(TableRef table) => _outgoing[table];

    /// <summary>Relationships where other tables reference <paramref name="table"/>.</summary>
    public IEnumerable<ForeignKeyInfo> IncomingOf(TableRef table) => _incoming[table];

    /// <summary>
    /// Own objects win over synonyms to them; several synonyms for the same object collapse into one entry,
    /// preferring a private synonym over a public one.
    /// </summary>
    internal static IReadOnlyList<TableSummary> Merge(IReadOnlyList<TableSummary> own, IReadOnlyList<TableSummary> synonymTargets) =>
        Merge(own, synonymTargets, t => t.Ref, t => t.Synonym, t => t.DisplayName, t => t.Owner);

    /// <summary>The same for PL/SQL units.</summary>
    internal static IReadOnlyList<PlSqlObjectSummary> Merge(IReadOnlyList<PlSqlObjectSummary> own, IReadOnlyList<PlSqlObjectSummary> synonymTargets) =>
        Merge(own, synonymTargets, o => o.Ref, o => o.Synonym, o => o.DisplayName, o => o.Owner);

    private static IReadOnlyList<T> Merge<T, TKey>(
        IReadOnlyList<T> own, IReadOnlyList<T> synonymTargets,
        Func<T, TKey> key, Func<T, SynonymInfo?> synonym, Func<T, string> displayName, Func<T, string> owner) where TKey : notnull
    {
        var ownRefs = own.Select(key).ToHashSet();
        var viaSynonym = synonymTargets
            .Where(t => !ownRefs.Contains(key(t)))
            .GroupBy(key)
            .Select(g => g.OrderBy(t => synonym(t)?.IsPublic ?? false).ThenBy(displayName, StringComparer.Ordinal).First());

        return own.Concat(viaSynonym)
            .OrderBy(displayName, StringComparer.Ordinal)
            .ThenBy(owner, StringComparer.Ordinal)
            .ToList();
    }
}
