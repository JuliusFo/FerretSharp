namespace FerretSharp.Core.Schema;

public enum TableKind
{
    Table,
    View,
    MaterializedView,
}

/// <summary>Exact (case-sensitive) owner and object name as stored in the data dictionary.</summary>
public sealed record TableRef(string Owner, string Name)
{
    public override string ToString() => $"{Owner}.{Name}";
}

/// <summary>A private (owner = the browsed schema) or public synonym through which an object is reached.</summary>
public sealed record SynonymInfo(string Owner, string Name)
{
    public const string PublicOwner = "PUBLIC";

    public bool IsPublic => Owner == PublicOwner;
}

/// <summary>
/// Loaded for every object of the schema on connect. <see cref="Owner"/>/<see cref="Name"/> always denote the real
/// object; objects of other schemas reached through a synonym carry it in <see cref="Synonym"/>.
/// </summary>
public sealed record TableSummary(string Owner, string Name, TableKind Kind, SynonymInfo? Synonym = null)
{
    public TableRef Ref => new(Owner, Name);

    /// <summary>The name users know the object by: the synonym if there is one.</summary>
    public string DisplayName => Synonym?.Name ?? Name;

    /// <summary>
    /// <c>ALL_OBJECTS.STATUS = 'INVALID'</c> (views and materialized views): needs recompiling. Oracle tries that on the
    /// next use of a view; a view whose table lost a column then fails. An invalid materialized view still answers.
    /// </summary>
    public bool IsInvalid { get; init; }
}

/// <param name="DataType">Oracle type name as in <c>ALL_TAB_COLUMNS.DATA_TYPE</c>, e.g. <c>VARCHAR2</c> or <c>TIMESTAMP(6)</c>.</param>
/// <param name="Length">Character length for character types (CHAR semantics) or byte length for RAW.</param>
/// <param name="CharSemantics">True if the length is in characters (<c>VARCHAR2(50 CHAR)</c>).</param>
/// <param name="Comment">Column comment (<c>ALL_COL_COMMENTS</c>).</param>
/// <param name="IsVirtual">Virtual column: computed from <see cref="Default"/>, which then holds the expression.</param>
/// <param name="DefaultOnNull"><c>DEFAULT ON NULL</c>: the default also replaces an explicit NULL.</param>
public sealed record ColumnInfo(
    string Name,
    string DataType,
    int? Length,
    bool CharSemantics,
    int? Precision,
    int? Scale,
    bool Nullable,
    bool IsIdentity,
    string? Default,
    int Position,
    string? Comment = null,
    bool IsVirtual = false,
    bool DefaultOnNull = false)
{
    /// <summary>The type as shown to the user, e.g. <c>VARCHAR2(50 CHAR)</c>, <c>NUMBER(12,2)</c>, <c>DATE</c> (<see cref="OracleTypes.DisplayType"/>).</summary>
    public string DisplayType => OracleTypes.DisplayType(this);
}

/// <summary>Lazily loaded per table.</summary>
/// <param name="Definition">SELECT behind a view or materialized view; null for tables.</param>
/// <param name="DefinitionTruncated">The definition is longer than what could be fetched.</param>
public sealed record TableDetails(
    TableSummary Table,
    IReadOnlyList<ColumnInfo> Columns,
    IReadOnlyList<string> PrimaryKey,
    IReadOnlyList<IReadOnlyList<string>> UniqueKeys,
    bool IsIndexOrganized,
    string? Definition = null,
    bool DefinitionTruncated = false)
{
    /// <summary>
    /// The primary key is enabled and validated, so its values are unique. A disabled key (or <c>ENABLE NOVALIDATE</c>)
    /// may hold duplicates: a table then addresses its rows by ROWID (review 3.31.0, A2). Constraints of views are never
    /// enforced; a (read-only) view keeps its declared key as its row identity.
    /// </summary>
    public bool PrimaryKeyEnforced { get; init; } = true;

    /// <summary>The position of a column by its exact dictionary name in <see cref="Columns"/>; -1 if the table has none.</summary>
    public int IndexOf(string column)
    {
        for (var i = 0; i < Columns.Count; i++)
        {
            if (Columns[i].Name == column)
            {
                return i;
            }
        }

        return -1;
    }
}

/// <summary>Where a relationship comes from.</summary>
public enum FkSource
{
    /// <summary>A foreign key constraint in the database.</summary>
    Declared,
    Manual,
    Convention,

    /// <summary>A relationship of the linked project's EF Core model without a constraint in the database (WP-12).</summary>
    ClrModel,
}

/// <summary>A (possibly composite) relationship; columns are listed in matching order.</summary>
public sealed record ForeignKeyInfo(
    string Name,
    TableRef From,
    IReadOnlyList<string> FromColumns,
    TableRef To,
    IReadOnlyList<string> ToColumns,
    FkSource Source);

/// <summary>The primary key of a table (enabled or not): its columns in key order.</summary>
public sealed record PrimaryKeyInfo(TableRef Table, IReadOnlyList<string> Columns);

public interface ISchemaReader
{
    /// <summary>Tables, views and materialized views of <paramref name="owner"/>, sorted by name.</summary>
    Task<IReadOnlyList<TableSummary>> GetTablesAsync(string owner, CancellationToken cancellationToken);

    /// <summary>
    /// Tables, views, materialized views and PL/SQL units (packages, procedures, functions) of other (non-Oracle)
    /// schemas that <paramref name="owner"/> reaches through its private synonyms or public synonyms, limited to
    /// objects the session may access. No DB-link synonyms.
    /// </summary>
    Task<SynonymTargets> GetSynonymTargetsAsync(string owner, CancellationToken cancellationToken);

    /// <summary>Packages, procedures, functions and triggers of <paramref name="owner"/>, sorted by name (WP-28).</summary>
    Task<IReadOnlyList<PlSqlObjectSummary>> GetPlSqlObjectsAsync(string owner, CancellationToken cancellationToken);

    /// <summary>Status and dates of a PL/SQL unit (both parts of a package), its AUTHID or, for a trigger, event and table.</summary>
    Task<PlSqlObjectInfo> GetPlSqlInfoAsync(PlSqlObjectSummary unit, CancellationToken cancellationToken);

    /// <summary>
    /// Source of one part from <c>ALL_SOURCE</c>, one entry per line. Empty if the part does not exist or the session may
    /// not see it (another schema's package body needs the DEBUG privilege on it).
    /// </summary>
    Task<PlSqlSource> GetSourceAsync(PlSqlObjectSummary unit, PlSqlPart part, CancellationToken cancellationToken);

    /// <summary>The procedures and functions of a unit with their parameters (<c>ALL_ARGUMENTS</c>), overloads separately; empty for a trigger.</summary>
    Task<IReadOnlyList<PlSqlSubprogram>> GetSubprogramsAsync(PlSqlObjectSummary unit, CancellationToken cancellationToken);

    /// <summary>
    /// The columns of its table a trigger references (ALL_TRIGGER_COLS: :new/:old in the body, UPDATE OF; not those of
    /// the WHEN clause), distinct, in dictionary form, ordered by name; empty if it references none, there is no such
    /// trigger or it is not VALID (an invalid trigger's entries can name the wrong columns).
    /// </summary>
    Task<IReadOnlyList<string>> GetTriggerColumnsAsync(string owner, string trigger, CancellationToken cancellationToken);

    /// <summary>Compile errors and warnings of both parts (<c>ALL_ERRORS</c>), specification first.</summary>
    Task<IReadOnlyList<PlSqlError>> GetErrorsAsync(PlSqlObjectSummary unit, CancellationToken cancellationToken);

    /// <summary>Dependencies of a PL/SQL unit in both directions, limited to objects the session may see.</summary>
    Task<ObjectDependencies> GetDependenciesAsync(PlSqlObjectSummary unit, CancellationToken cancellationToken);

    /// <summary>
    /// Lines of the schema's PL/SQL source containing <paramref name="text"/> (ignoring case), at most
    /// <paramref name="limit"/>, by object and line. Scans all source of the schema.
    /// </summary>
    Task<IReadOnlyList<SourceHit>> SearchSourceAsync(string owner, string text, int limit, CancellationToken cancellationToken);

    /// <summary>All declared foreign keys whose referencing table belongs to <paramref name="owner"/>.</summary>
    Task<IReadOnlyList<ForeignKeyInfo>> GetForeignKeysAsync(string owner, CancellationToken cancellationToken);

    /// <summary>The primary keys of all tables of <paramref name="owner"/> (base tables of FK jumps).</summary>
    Task<IReadOnlyList<PrimaryKeyInfo>> GetPrimaryKeysAsync(string owner, CancellationToken cancellationToken);

    Task<TableDetails> GetDetailsAsync(TableSummary table, CancellationToken cancellationToken);

    /// <summary>Status, dates, comment and statistics of one object.</summary>
    Task<ObjectInfo> GetObjectInfoAsync(TableSummary table, CancellationToken cancellationToken);

    /// <summary>All constraints of the object, primary key first.</summary>
    Task<IReadOnlyList<ConstraintInfo>> GetConstraintsAsync(TableRef table, CancellationToken cancellationToken);

    /// <summary>Indexes on the table (without LOB indexes), by name.</summary>
    Task<IReadOnlyList<IndexInfo>> GetIndexesAsync(TableRef table, CancellationToken cancellationToken);

    /// <summary>Dependencies in both directions, limited to objects the session may see.</summary>
    Task<ObjectDependencies> GetDependenciesAsync(TableSummary table, CancellationToken cancellationToken);

    /// <summary>
    /// DDL from <c>DBMS_METADATA.GET_DDL</c>. Other schemas need <c>SELECT_CATALOG_ROLE</c>; without it Oracle
    /// answers ORA-31603 (object not found).
    /// </summary>
    Task<string> GetDdlAsync(TableSummary table, CancellationToken cancellationToken);

    /// <summary>
    /// Sessions (other than this one) holding locks on the table – for the lock conflict dialog. Null if the user
    /// may not read <c>V$LOCKED_OBJECT</c>/<c>V$SESSION</c> (needs <c>SELECT_CATALOG_ROLE</c> or grants).
    /// </summary>
    Task<IReadOnlyList<Data.LockHolder>?> GetLockHoldersAsync(TableRef table, CancellationToken cancellationToken);

    /// <summary>
    /// What the session with this tag is doing (WP-52, ADR 0027): the lock it waits for and its latest long operation, from
    /// <c>V$SESSION</c>/<c>V$SESSION_LONGOPS</c>. <see cref="Data.SessionActivity.None"/> if there is nothing to say or the
    /// session is not visible; null if the user may not read the V$ views.
    /// </summary>
    Task<Data.SessionActivity?> GetSessionActivityAsync(string sessionTag, CancellationToken cancellationToken);

    /// <summary>
    /// The columns of every table, view and materialized view of a schema in one query (C# model comparison, where one
    /// query per table costs minutes over a slow network). Table name → columns in column order, without
    /// <see cref="ColumnInfo.Default"/> and <see cref="ColumnInfo.Comment"/>.
    /// </summary>
    Task<IReadOnlyDictionary<string, IReadOnlyList<ColumnInfo>>> GetColumnsAsync(string owner, CancellationToken cancellationToken);

    /// <summary>
    /// The structure of a whole schema for the schema comparison (WP-20): tables, views and materialized views with
    /// columns, constraints and indexes, in a few queries for the whole schema (not one per table: slow over a VPN).
    /// </summary>
    /// <param name="progress">The step being read ('Spalten', 'Constraints' …); may be called on any thread.</param>
    Task<Compare.SchemaSnapshot> ReadSnapshotAsync(string owner, IProgress<string>? progress, CancellationToken cancellationToken);

    /// <summary>
    /// The optimizer's estimated plan of a query (ADR 0012), without running it and without bind values. On the
    /// explorer session, which has no transaction: Oracle refuses EXPLAIN PLAN in a read-only one.
    /// </summary>
    Task<Query.ExecutionPlan> ExplainAsync(Query.QuerySpec query, CancellationToken cancellationToken);

    /// <summary>
    /// What the shared pool knows about a statement another program ran (WP-33): <c>V$SQL</c> figures and the plan of the
    /// child used last, found by the SQL_ID of the exact text; null if the statement is not (or no longer) there. On the
    /// explorer session; nothing is run.
    /// </summary>
    /// <exception cref="Query.PlanUnavailableException">No read access to the V$ views.</exception>
    Task<Query.CursorInfo?> GetCursorAsync(string sql, CancellationToken cancellationToken);
}
