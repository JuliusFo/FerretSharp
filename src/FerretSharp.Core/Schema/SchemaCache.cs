using System.Collections.Concurrent;

namespace FerretSharp.Core.Schema;

/// <summary>
/// Schema metadata for one connection: object list and foreign keys are loaded up front, column details lazily
/// per table (ALL_TAB_COLUMNS is slow on large databases). <see cref="RefreshAsync"/> drops everything.
/// </summary>
public sealed class SchemaCache(ISchemaReader reader, string owner)
{
    private ConcurrentDictionary<TableRef, Lazy<Task<TableDetails>>> _details = new();
    private Dictionary<TableRef, TableSummary> _byRef = [];
    private ILookup<TableRef, ForeignKeyInfo> _outgoing = Array.Empty<ForeignKeyInfo>().ToLookup(f => f.From);
    private ILookup<TableRef, ForeignKeyInfo> _incoming = Array.Empty<ForeignKeyInfo>().ToLookup(f => f.To);

    public string Owner { get; } = owner;

    public IReadOnlyList<TableSummary> Tables { get; private set; } = [];

    public IReadOnlyList<ForeignKeyInfo> ForeignKeys { get; private set; } = [];

    public DateTimeOffset? LoadedAt { get; private set; }

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        var tables = await reader.GetTablesAsync(Owner, cancellationToken);
        var foreignKeys = await reader.GetForeignKeysAsync(Owner, cancellationToken);

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

    /// <summary>Relationships where <paramref name="table"/> references other tables.</summary>
    public IEnumerable<ForeignKeyInfo> OutgoingOf(TableRef table) => _outgoing[table];

    /// <summary>Relationships where other tables reference <paramref name="table"/>.</summary>
    public IEnumerable<ForeignKeyInfo> IncomingOf(TableRef table) => _incoming[table];
}
