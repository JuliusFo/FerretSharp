namespace FerretSharp.Core.Schema;

/// <summary>How many of the counted tables or indexes lie in a tablespace.</summary>
public sealed record TablespaceUse(string Name, int Count);

/// <summary>Where the suggestion of a tablespace comes from.</summary>
public enum TablespaceBasis
{
    /// <summary>The indexes of the table at hand.</summary>
    TableIndexes,

    /// <summary>The indexes of the owner's tables.</summary>
    SchemaIndexes,

    /// <summary>The owner's tables.</summary>
    SchemaTables,
}

/// <summary>A tablespace worth offering: most of <paramref name="Of"/> counted objects (<paramref name="Basis"/>) lie in it.</summary>
public sealed record TablespaceSuggestion(string Name, int Count, int Of, TablespaceBasis Basis);

/// <summary>
/// What the designer and the index draft offer for tablespaces (WP-67, <see cref="ISchemaReader.GetTablespacesAsync"/>): the
/// tablespaces to choose from, the owner's default, the quotas, and where the owner's tables and indexes lie – the field
/// starts empty (the default), the suggestion follows what is there.
/// </summary>
/// <param name="Names">Permanent, online tablespaces (<c>USER_TABLESPACES</c>), by name.</param>
/// <param name="Default">The owner's default tablespace; known only for the session's own schema (<c>USER_USERS</c>), null otherwise.</param>
/// <param name="Quota">
/// Tablespaces the owner may fill (<c>USER_TS_QUOTAS</c>); null when there is nothing to check: <c>UNLIMITED TABLESPACE</c>, or
/// another schema (its quotas need DBA views).
/// </param>
/// <param name="TableIndexes">Where the table's indexes lie (without LOB indexes); empty for a new table.</param>
/// <param name="SchemaIndexes">Where the owner's indexes lie (without LOB indexes).</param>
/// <param name="SchemaTables">Where the owner's tables lie (without temporary and partitioned ones: they name none).</param>
public sealed record Tablespaces(
    IReadOnlyList<string> Names,
    string? Default,
    IReadOnlySet<string>? Quota,
    IReadOnlyList<TablespaceUse> TableIndexes,
    IReadOnlyList<TablespaceUse> SchemaIndexes,
    IReadOnlyList<TablespaceUse> SchemaTables)
{
    /// <summary>Nothing read (yet): no choice, no suggestion, no warning.</summary>
    public static readonly Tablespaces None = new([], null, null, [], [], []);

    /// <summary>
    /// The table is a global temporary one: its indexes live in the temporary tablespace. Oracle ignores <c>TABLESPACE</c>
    /// on such an index and fails <c>USING INDEX TABLESPACE</c> with ORA-00600 (measured against Oracle 23, WP-67 review).
    /// </summary>
    public bool TemporaryTable { get; init; }

    /// <summary>The tablespace the table lies in; null for a new, temporary or partitioned table. Its audit table follows it.</summary>
    public string? TableTablespace { get; init; }

    /// <summary>
    /// The owner has no quota in it: Oracle refuses the first row (ORA-01950) – an index on rows at once, a table or an index
    /// on an empty table only with its first row (deferred segment creation, measured against Oracle 23).
    /// </summary>
    public bool LacksQuota(string? tablespace) => !string.IsNullOrEmpty(tablespace) && Quota is { } quota && !quota.Contains(tablespace);

    /// <summary>For new indexes: where most of the table's indexes lie, without any where most of the schema's lie.</summary>
    public TablespaceSuggestion? ForIndexes() =>
        Suggest(TableIndexes, TablespaceBasis.TableIndexes) ?? (TableIndexes.Count == 0 ? Suggest(SchemaIndexes, TablespaceBasis.SchemaIndexes) : null);

    /// <summary>For a new table: where most of the schema's tables lie.</summary>
    public TablespaceSuggestion? ForTable() => Suggest(SchemaTables, TablespaceBasis.SchemaTables);

    /// <summary>The most used one (ties by name) – unless it is the default anyway, or no longer to choose.</summary>
    private TablespaceSuggestion? Suggest(IReadOnlyList<TablespaceUse> uses, TablespaceBasis basis)
    {
        if (uses.Count == 0)
        {
            return null;
        }

        var top = uses.OrderByDescending(u => u.Count).ThenBy(u => u.Name, StringComparer.Ordinal).First();
        return top.Name == Default || !Names.Contains(top.Name, StringComparer.Ordinal)
            ? null
            : new TablespaceSuggestion(top.Name, top.Count, uses.Sum(u => u.Count), basis);
    }
}
