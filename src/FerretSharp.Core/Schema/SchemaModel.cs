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
}

/// <param name="DataType">Oracle type name as in <c>ALL_TAB_COLUMNS.DATA_TYPE</c>, e.g. <c>VARCHAR2</c> or <c>TIMESTAMP(6)</c>.</param>
/// <param name="Length">Character length for character types (CHAR semantics) or byte length for RAW.</param>
/// <param name="CharSemantics">True if the length is in characters (<c>VARCHAR2(50 CHAR)</c>).</param>
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
    int Position)
{
    /// <summary>Type as it would appear in DDL, e.g. <c>VARCHAR2(50 CHAR)</c>, <c>NUMBER(12,2)</c>, <c>DATE</c>.</summary>
    public string DisplayType => DataType switch
    {
        "VARCHAR2" or "NVARCHAR2" or "CHAR" or "NCHAR" when Length is { } len =>
            CharSemantics && DataType is "VARCHAR2" or "CHAR" ? $"{DataType}({len} CHAR)" : $"{DataType}({len})",
        "RAW" when Length is { } len => $"RAW({len})",
        "NUMBER" when Precision is { } p && Scale is { } s and not 0 => $"NUMBER({p},{s})",
        "NUMBER" when Precision is { } p => $"NUMBER({p})",
        "NUMBER" when Scale is 0 => "INTEGER",
        "FLOAT" when Precision is { } p => $"FLOAT({p})",
        _ => DataType,
    };
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
    bool DefinitionTruncated = false);

/// <summary>Where a relationship comes from. <c>ClrModel</c> (EF Core navigations) follows in v3.</summary>
public enum FkSource
{
    Declared,
    Manual,
    Convention,
}

/// <summary>A (possibly composite) relationship; columns are listed in matching order.</summary>
public sealed record ForeignKeyInfo(
    string Name,
    TableRef From,
    IReadOnlyList<string> FromColumns,
    TableRef To,
    IReadOnlyList<string> ToColumns,
    FkSource Source);

public interface ISchemaReader
{
    /// <summary>Tables, views and materialized views of <paramref name="owner"/>, sorted by name.</summary>
    Task<IReadOnlyList<TableSummary>> GetTablesAsync(string owner, CancellationToken cancellationToken);

    /// <summary>
    /// Tables, views and materialized views of other (non-Oracle) schemas that <paramref name="owner"/> reaches through
    /// its private synonyms or public synonyms, limited to objects the session may access. No DB-link synonyms.
    /// </summary>
    Task<IReadOnlyList<TableSummary>> GetSynonymTargetsAsync(string owner, CancellationToken cancellationToken);

    /// <summary>All declared foreign keys whose referencing table belongs to <paramref name="owner"/>.</summary>
    Task<IReadOnlyList<ForeignKeyInfo>> GetForeignKeysAsync(string owner, CancellationToken cancellationToken);

    Task<TableDetails> GetDetailsAsync(TableSummary table, CancellationToken cancellationToken);
}
