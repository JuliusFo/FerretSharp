using System.Collections.Concurrent;
using FerretSharp.Core.Audit;
using FerretSharp.Core.Resources;

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
    private Dictionary<TableRef, IReadOnlyList<string>> _primaryKeys = [];
    private Func<AuditTemplate?>? _auditTemplate;
    private AuditTemplate? _template;
    private AuditTables? _auditTables;

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

    public Task LoadAsync(CancellationToken cancellationToken) => LoadAsync(null, cancellationToken);

    /// <param name="progress">Each query before it runs (connecting shows the step): one after the other on the explorer session.</param>
    public async Task LoadAsync(IProgress<string>? progress, CancellationToken cancellationToken)
    {
        progress?.Report(ConnectionText.Step_ReadingTables);
        var own = await reader.GetTablesAsync(Owner, cancellationToken);
        progress?.Report(ConnectionText.Step_ReadingSynonyms);
        var synonymTargets = await reader.GetSynonymTargetsAsync(Owner, cancellationToken);
        var tables = Merge(own, synonymTargets.Tables);
        progress?.Report(ConnectionText.Step_ReadingPlSql);
        var plSql = Merge(await reader.GetPlSqlObjectsAsync(Owner, cancellationToken), synonymTargets.PlSql);

        // Foreign keys and primary keys of every schema that contributes objects, so relationships work across synonyms too.
        var schemas = tables.Select(t => t.Owner).Prepend(Owner).Distinct(StringComparer.Ordinal).ToList();
        progress?.Report(TextFormat.Format(ConnectionText.Step_ReadingKeys, string.Join(", ", schemas)));
        var foreignKeys = (await reader.GetForeignKeysAsync(schemas, cancellationToken)).ToList();
        var primaryKeys = new Dictionary<TableRef, IReadOnlyList<string>>();
        foreach (var key in await reader.GetPrimaryKeysAsync(schemas, cancellationToken))
        {
            primaryKeys[key.Table] = key.Columns;
        }

        Tables = tables;
        _byRef = tables.ToDictionary(t => t.Ref);
        PlSqlObjects = plSql;
        _plSqlByRef = plSql.ToDictionary(o => o.Ref);
        _primaryKeys = primaryKeys;
        lock (_foreignKeyLock)
        {
            _declared = foreignKeys;
            CombineForeignKeys();
        }

        _details = new ConcurrentDictionary<TableRef, Lazy<Task<TableDetails>>>();
        RefreshAuditTemplate();
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

    /// <summary>
    /// The name people know a table by here: the synonym it is reached through, its name in this schema, otherwise owner
    /// and name (another schema without synonym, or a table the cache does not list). Links, the relationship diagram and
    /// its Mermaid export all name tables so (WP-46 review: they had three versions of this rule).
    /// </summary>
    public string LabelOf(TableRef table) =>
        Find(table) is { Synonym: not null } summary ? summary.DisplayName
        : table.Owner == Owner ? table.Name
        : table.ToString();

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

    public Task<IReadOnlyList<string>> GetTriggerColumnsAsync(string owner, string trigger, CancellationToken cancellationToken) =>
        reader.GetTriggerColumnsAsync(owner, trigger, cancellationToken);

    /// <summary>Enabled triggers on the table's DML, as owner and name (WP-42); not cached.</summary>
    public Task<IReadOnlyList<TableRef>> GetTableTriggersAsync(TableRef table, CancellationToken cancellationToken) =>
        reader.GetTableTriggersAsync(table, cancellationToken);

    public Task<ObjectDependencies> GetDependenciesAsync(PlSqlObjectSummary unit, CancellationToken cancellationToken) =>
        reader.GetDependenciesAsync(unit, cancellationToken);

    /// <summary>Lines of the schema's own PL/SQL containing the text (owner filter: synonym targets are not searched).</summary>
    public Task<IReadOnlyList<SourceHit>> SearchSourceAsync(string text, int limit, CancellationToken cancellationToken) =>
        reader.SearchSourceAsync(Owner, text, limit, cancellationToken);

    public Task<IReadOnlyList<Data.LockHolder>?> GetLockHoldersAsync(TableRef table, CancellationToken cancellationToken) =>
        reader.GetLockHoldersAsync(table, cancellationToken);

    /// <summary>
    /// What a workspace's session is doing (WP-52); null without rights on the V$ views. The first refusal is remembered for
    /// this connection: the next statements ask no more (a new connection, after reconnecting, tries again).
    /// </summary>
    public async Task<Data.SessionActivity?> GetSessionActivityAsync(string sessionTag, CancellationToken cancellationToken)
    {
        if (SessionActivityUnavailable)
        {
            return null;
        }

        var activity = await reader.GetSessionActivityAsync(sessionTag, cancellationToken);
        SessionActivityUnavailable = activity is null;
        return activity;
    }

    /// <summary>The user may not read <c>V$SESSION</c>/<c>V$SESSION_LONGOPS</c> (seen once on this connection).</summary>
    public bool SessionActivityUnavailable { get; private set; }

    /// <summary>Columns of all objects of a schema in one query (not cached: the C# model comparison reads them once).</summary>
    public Task<IReadOnlyDictionary<string, IReadOnlyList<ColumnInfo>>> GetColumnsAsync(string owner, CancellationToken cancellationToken) =>
        reader.GetColumnsAsync(owner, cancellationToken);

    /// <summary>The estimated plan of a query (explorer session, ADR 0012).</summary>
    public Task<Query.ExecutionPlan> ExplainAsync(Query.QuerySpec query, CancellationToken cancellationToken) =>
        reader.ExplainAsync(query, cancellationToken);

    /// <summary>V$SQL figures and plan of a statement another program ran (explorer session, WP-33).</summary>
    public Task<Query.CursorInfo?> GetCursorAsync(string sql, CancellationToken cancellationToken) =>
        reader.GetCursorAsync(sql, cancellationToken);

    /// <summary>Relationships where <paramref name="table"/> references other tables, as the database and the C# model have them.</summary>
    public IEnumerable<ForeignKeyInfo> OutgoingOf(TableRef table) => _outgoing[table];

    /// <summary>
    /// The relationships to follow from rows of <paramref name="table"/> (grid, form, menus, names beside foreign keys):
    /// <see cref="OutgoingOf"/>, and for an audit table (WP-63, ADR 0036) its base table's too, copied onto its columns
    /// (<see cref="AuditRelation.Copied"/>; one of the base table to itself still points to the base table), and one to its
    /// base row (<see cref="AuditRelation.BaseRow"/>). Nothing else sees these: <see cref="OutgoingOf"/>,
    /// <see cref="IncomingOf"/>, <see cref="ForeignKeys"/> and the diagrams stay the database's – nothing leads into an
    /// audit table, and nothing takes a copy for a constraint.
    /// </summary>
    public IEnumerable<ForeignKeyInfo> LinksOf(TableRef table) =>
        AuditTablesNow() is { } audit && audit.BaseOf.ContainsKey(table) ? audit.Outgoing(table) : _outgoing[table];

    /// <summary>
    /// Where the audit template of the connection comes from (WP-63): which table is the audit table of which follows its
    /// naming rule. Asked when the schema loads and on <see cref="RefreshAuditTemplate"/> – never per lookup: the file may
    /// sit on a share over the VPN, and lookups run while the grid renders.
    /// </summary>
    public void UseAuditTemplate(Func<AuditTemplate?> template) => _auditTemplate = template;

    /// <summary>Asks for the audit template again (a table tab loads: an edited template counts from then on, as in the audit line).</summary>
    public void RefreshAuditTemplate() => _template = _auditTemplate?.Invoke();

    /// <summary>The audit template as last asked (<see cref="RefreshAuditTemplate"/>); null without one or if it cannot be used.</summary>
    public AuditTemplate? AuditTemplate => _template;

    /// <summary>The base table of an audit table by the template's names (WP-63); null for any other table or without a template.</summary>
    public TableRef? AuditBaseOf(TableRef table) => AuditTablesNow()?.BaseOf.GetValueOrDefault(table);

    /// <summary>The audit tables of the current template, table list and relationships; built again when one of them changed.</summary>
    private AuditTables? AuditTablesNow()
    {
        if (_template is not { } template)
        {
            return null;
        }

        var current = _auditTables;
        var foreignKeys = ForeignKeys;
        if (current is not null && ReferenceEquals(current.Template, template) && ReferenceEquals(current.Tables, Tables)
            && ReferenceEquals(current.ForeignKeys, foreignKeys))
        {
            return current;
        }

        var own = Tables.Where(t => t.Kind == TableKind.Table && t.Synonym is null).Select(t => t.Ref);
        current = new AuditTables(template, Tables, foreignKeys, AuditStatus.Pairs(template, own), _outgoing, PrimaryKeyOf);
        _auditTables = current;
        return current;
    }

    /// <summary>Audit table → base table, and the relationships each audit table gets from its base table (built on first look).</summary>
    private sealed class AuditTables(
        AuditTemplate template, IReadOnlyList<TableSummary> tables, IReadOnlyList<ForeignKeyInfo> foreignKeys,
        IReadOnlyDictionary<TableRef, TableRef> baseOf, ILookup<TableRef, ForeignKeyInfo> outgoing, Func<TableRef, IReadOnlyList<string>> primaryKeyOf)
    {
        private readonly ConcurrentDictionary<TableRef, IReadOnlyList<ForeignKeyInfo>> _outgoing = new();

        public AuditTemplate Template => template;

        public IReadOnlyList<TableSummary> Tables => tables;

        public IReadOnlyList<ForeignKeyInfo> ForeignKeys => foreignKeys;

        public IReadOnlyDictionary<TableRef, TableRef> BaseOf => baseOf;

        public IReadOnlyList<ForeignKeyInfo> Outgoing(TableRef audit) => _outgoing.GetOrAdd(audit, _ =>
        {
            var baseTable = baseOf[audit];
            var own = outgoing[audit].ToList(); // declared on the audit table itself (rare): kept
            var copied = outgoing[baseTable].Select(fk => fk with { From = audit, Audit = AuditRelation.Copied });
            var key = primaryKeyOf(baseTable);
            IEnumerable<ForeignKeyInfo> baseRow = key.Count == 0
                ? []
                : [new ForeignKeyInfo(baseTable.Name, audit, key, baseTable, key, FkSource.Convention) { Audit = AuditRelation.BaseRow }];
            return [.. own, .. baseRow, .. copied];
        });
    }

    /// <summary>Relationships where other tables reference <paramref name="table"/>.</summary>
    public IEnumerable<ForeignKeyInfo> IncomingOf(TableRef table) => _incoming[table];

    /// <summary>
    /// The primary key columns of <paramref name="table"/> (enabled or not; a view's declarative key too); empty for a table
    /// without one or one outside the loaded schemas.
    /// </summary>
    public IReadOnlyList<string> PrimaryKeyOf(TableRef table) => _primaryKeys.GetValueOrDefault(table) ?? [];

    /// <summary>
    /// Relationships into the base tables of <paramref name="table"/> that reach its rows too, by their shared primary key
    /// (<see cref="BaseTables"/>).
    /// </summary>
    public IReadOnlyList<InheritedReference> InheritedIncomingOf(TableRef table) =>
        BaseTables.ReferencesOf(table, PrimaryKeyOf, OutgoingOf, IncomingOf);

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
