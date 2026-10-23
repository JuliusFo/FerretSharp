using FerretSharp.Core.Query;
using FerretSharp.Core.Schema;

namespace FerretSharp.Core.Data;

/// <summary>Identifies a row for navigation (v1) and later for UPDATE/DELETE (v2). See docs/architecture.md 1.5.</summary>
public abstract record RowKey
{
    /// <summary>
    /// Compares by value: two keys with the same column values are equal. RAW values (<c>byte[]</c>, e.g. GUIDs as
    /// EF Core stores them) compare by content – every read returns a new array, and the change tracker must still
    /// find the row after a reload.
    /// </summary>
    public sealed record PrimaryKey(IReadOnlyList<object?> Values) : RowKey
    {
        public bool Equals(PrimaryKey? other) => other is not null && Values.SequenceEqual(other.Values, KeyValueComparer.Instance);

        public override int GetHashCode()
        {
            var hash = new HashCode();
            foreach (var value in Values)
            {
                hash.Add(value, KeyValueComparer.Instance);
            }

            return hash.ToHashCode();
        }
    }

    public sealed record RowId(string Value) : RowKey;

    public sealed record None : RowKey
    {
        public static readonly None Instance = new();
    }

    /// <summary>Key values: <c>byte[]</c> by content, everything else by <see cref="object.Equals(object, object)"/>.</summary>
    private sealed class KeyValueComparer : IEqualityComparer<object?>
    {
        public static readonly KeyValueComparer Instance = new();

        public new bool Equals(object? x, object? y) =>
            x is byte[] a && y is byte[] b ? a.AsSpan().SequenceEqual(b) : object.Equals(x, y);

        public int GetHashCode(object? value)
        {
            if (value is not byte[] bytes)
            {
                return value?.GetHashCode() ?? 0;
            }

            var hash = new HashCode();
            hash.AddBytes(bytes);
            return hash.ToHashCode();
        }
    }
}

/// <summary>A NUMBER that does not fit into <see cref="decimal"/> (up to 38 digits); kept as invariant text.</summary>
public sealed record BigNumber(string Invariant)
{
    public override string ToString() => Invariant;
}

/// <summary>CLOB: first characters plus total length. BLOB: length only (<see cref="Preview"/> is null).</summary>
public sealed record LobValue(string? Preview, long Length)
{
    /// <summary>
    /// The grid form of a whole LOB value (a text or bytes from the LOB editor, WP-10): preview and length like a
    /// loaded row. Anything else is returned unchanged.
    /// </summary>
    public static object? FromContent(object? value) => value switch
    {
        string text => new LobValue(text.Length > Query.QueryBuilder.ClobPreviewLength ? text[..Query.QueryBuilder.ClobPreviewLength] : text, text.Length),
        byte[] bytes => new LobValue(null, bytes.Length),
        _ => value,
    };
}

/// <summary>Value of a column that is only checked for NULL (LONG, XMLTYPE, object types …).</summary>
public sealed record NotNullMarker(string DataType);

public sealed record RowData(RowKey Key, IReadOnlyList<object?> Values);

/// <param name="IsLastPage">Fewer rows than requested were returned.</param>
/// <param name="DataAsOf">Start of the read-only snapshot the page was read in (locked profiles); null otherwise.</param>
public sealed record RowPage(IReadOnlyList<RowData> Rows, bool IsLastPage, TimeSpan Elapsed, DateTimeOffset? DataAsOf = null);

/// <summary>Looks up display values (WP-31): through the workspace's data access, or within a whole read (WP-54).</summary>
public interface IRowLookup
{
    /// <summary>
    /// Key and display value of the rows of <paramref name="target"/> whose key column holds one of <paramref name="keys"/>
    /// (display columns, WP-31; <see cref="QueryBuilder.BuildLookup"/>). In the workspace's session, so it sees the
    /// workspace's own uncommitted rows; it does not start a new read-only snapshot. Keys without a row are not returned.
    /// </summary>
    Task<IReadOnlyList<LookupRow>> LookupAsync(
        TableDetails target, int keyColumn, int displayColumn, IReadOnlyList<object> keys, CancellationToken cancellationToken);
}

/// <summary>
/// One block of a whole read (<see cref="IDataAccess.ReadAllAsync"/>, WP-54) for its consumer, with a lookup that runs in the
/// same session while the read is open. False stops the read (an Excel sheet is full).
/// </summary>
public delegate Task<bool> RowBlockHandler(IReadOnlyList<RowData> rows, IRowLookup lookup, CancellationToken cancellationToken);

public interface IDataAccess : IRowLookup
{
    /// <summary>Reads one page; values are in <see cref="TableDetails.Columns"/> order.</summary>
    /// <exception cref="QueryValidationException">A filter or sort cannot be applied.</exception>
    Task<RowPage> ReadPageAsync(
        TableDetails table,
        IReadOnlyList<FilterCondition> filters,
        IReadOnlyList<SortSpec> sorts,
        PageSpec page,
        CancellationToken cancellationToken);

    Task<long> CountAsync(TableDetails table, IReadOnlyList<FilterCondition> filters, CancellationToken cancellationToken);

    /// <summary>
    /// The whole value of a LOB column (LOB editor, WP-10): text for CLOB/NCLOB, bytes for BLOB, null for NULL. Read in
    /// the workspace's session, so it includes the workspace's own uncommitted changes.
    /// </summary>
    Task<LobRead> ReadLobAsync(TableDetails table, RowKey key, int column, CancellationToken cancellationToken);

    /// <summary>
    /// A query FerretSharp did not build (LINQ console, ADR 0011): run as it is – only plain queries pass the session's
    /// read-only tripwire – and read rows <paramref name="skip"/> to <paramref name="skip"/> + <paramref name="take"/>.
    /// Later pages run the query again and skip rows: wrapping it (<c>SELECT * FROM (…)</c>) fails on the duplicate column
    /// names of EF's joins.
    /// </summary>
    /// <exception cref="InvalidOperationException">The statement is not a plain query.</exception>
    Task<SqlPage> ReadSqlAsync(QuerySpec query, int skip, int take, CancellationToken cancellationToken);

    /// <summary>
    /// The actual plan of a query (ADR 0012): runs it once more with <c>GATHER_PLAN_STATISTICS</c> – fetching the first
    /// <see cref="ActualPlanPageSize"/> rows, or all of them – and reads the cursor's plan with the run's numbers from
    /// <c>V$SQL_PLAN_STATISTICS_ALL</c>. Read only; in the workspace's snapshot or transaction.
    /// </summary>
    /// <exception cref="PlanUnavailableException">No rights on V$SQL / V$SQL_PLAN_STATISTICS_ALL, or the cursor is gone.</exception>
    Task<ExecutionPlan> ExplainActualAsync(QuerySpec query, bool wholeResult, CancellationToken cancellationToken);

    /// <summary>
    /// Every row of a table's query – filters and sort order as in the grid, LOBs as preview – handed to
    /// <paramref name="onBlock"/> in blocks of <see cref="BlockSize"/> (the export of all rows of a filter, WP-54). One
    /// cursor instead of pages: a large OFFSET gets slower page by page. Read in the session as it stands – no new snapshot
    /// in a locked workspace, so the rows are those of the grid's snapshot – and not repeated in a new one: a snapshot that
    /// becomes unusable meanwhile (ORA-01555) is reported. The session is busy until the read ends.
    /// </summary>
    /// <exception cref="QueryValidationException">A filter or sort cannot be applied.</exception>
    Task ReadAllAsync(
        TableDetails table,
        IReadOnlyList<FilterCondition> filters,
        IReadOnlyList<SortSpec> sorts,
        RowBlockHandler onBlock,
        CancellationToken cancellationToken);

    /// <summary>
    /// Every row of a free query (SQL editor), like <see cref="ReadAllAsync"/>: run once more, values as
    /// <see cref="ReadSqlAsync"/> reads them, rows without key.
    /// </summary>
    /// <param name="columns">The column names the result had: a query that now returns other columns is refused.</param>
    /// <exception cref="Connections.RefusedException">The query returns other columns than <paramref name="columns"/>.</exception>
    Task ReadSqlAllAsync(QuerySpec query, IReadOnlyList<string> columns, RowBlockHandler onBlock, CancellationToken cancellationToken);

    /// <summary>
    /// The values of columns for every row of a table's query (copy the values of a column, WP-57, or of marked columns,
    /// WP-58; <see cref="QueryBuilder.BuildColumnValues"/>), read like <see cref="ReadAllAsync"/>: one cursor in the session
    /// as it stands. Each row holds the values of <paramref name="columns"/> in their order, without key.
    /// </summary>
    /// <param name="columns">Indexes into the table's columns.</param>
    /// <param name="distinct">Each value once, ordered by the value (an IN list, one column); otherwise in the grid's order.</param>
    /// <exception cref="QueryValidationException">A filter or sort cannot be applied.</exception>
    Task ReadColumnAllAsync(
        TableDetails table,
        IReadOnlyList<int> columns,
        IReadOnlyList<FilterCondition> filters,
        IReadOnlyList<SortSpec> sorts,
        bool distinct,
        RowBlockHandler onBlock,
        CancellationToken cancellationToken);

    /// <summary>
    /// The rows of a table whose key columns hold one of the keys – the rows a relationship leads to (WP-42,
    /// <see cref="QueryBuilder.BuildRowsByKeys"/>), in statements of at most <see cref="QueryBuilder.MaxLookupKeys"/> keys. In
    /// the workspace's session, so it sees the workspace's own uncommitted rows; values as the grid reads them, LOBs whole if
    /// asked for. A key that finds no row returns nothing; one that finds several returns all of them.
    /// </summary>
    /// <param name="keyColumns">Indexes into the table's columns; null for ROWIDs (one text per key).</param>
    /// <param name="keys">Distinct keys, values in the order of <paramref name="keyColumns"/>, none NULL.</param>
    /// <param name="wholeLobs">LOB columns as whole values (a copy writes them), not as preview and length.</param>
    /// <param name="maxRows">
    /// Stops reading after this many rows of all statements together; null for all. A caller with a limit passes one more
    /// than it takes, to tell that there were more (WP-60: a customer with 50 000 orders is not read whole over the VPN).
    /// </param>
    Task<IReadOnlyList<RowData>> ReadByKeysAsync(
        TableDetails table,
        IReadOnlyList<int>? keyColumns,
        IReadOnlyList<IReadOnlyList<object>> keys,
        bool wholeLobs,
        int? maxRows,
        CancellationToken cancellationToken);

    /// <summary>Rows fetched for the actual plan unless the whole result is wanted: one grid page.</summary>
    public const int ActualPlanPageSize = 500;

    /// <summary>Rows per block of <see cref="ReadAllAsync"/>: a grid block.</summary>
    public const int BlockSize = 500;
}

/// <summary>A result column of a free query; <see cref="Column"/> is derived from the driver's type, for formatting.</summary>
/// <param name="Origin">Where the driver says the column was read from; null for expressions (WP-48).</param>
public sealed record SqlColumn(string Name, ColumnInfo Column, SqlColumnOrigin? Origin = null);

/// <summary>
/// The origin of a result column as ODP.NET reports it (<c>BaseSchemaName</c>, <c>BaseTableName</c>, <c>BaseColumnName</c>).
/// The driver reads the statement on the client: the table is the name as written (a synonym, a view), the owner only if
/// written, and a quoted alias (EF Core's <c>"a"</c>) comes back as the alias. A hint for <see cref="Query.SqlResultOrigins"/>,
/// never a fact (protocol of WP-48).
/// </summary>
/// <param name="Table">Table, view, synonym or alias as written; null if the driver names none (<c>WITH</c>, <c>USING</c>).</param>
/// <param name="Column">The column's dictionary name (<c>KUNDE_ID</c> behind <c>KUNDE_ID AS ID</c>).</param>
public sealed record SqlColumnOrigin(string? Owner, string? Table, string Column);

/// <param name="Rows">Values in <paramref name="Columns"/> order (LOBs whole, <see cref="BigNumber"/> beyond 28 digits).</param>
/// <param name="DataAsOf">Start of the read-only snapshot (locked workspaces); null otherwise.</param>
public sealed record SqlPage(
    IReadOnlyList<SqlColumn> Columns, IReadOnlyList<IReadOnlyList<object?>> Rows, bool IsLastPage, TimeSpan Elapsed, DateTimeOffset? DataAsOf = null);

/// <summary>A row found by <see cref="IRowLookup.LookupAsync"/>: its key and display value as read (like grid values).</summary>
public sealed record LookupRow(object Key, object? Display);

/// <summary>Result of <see cref="IDataAccess.ReadLobAsync"/>; <see cref="Found"/> is false if the row is gone.</summary>
public sealed record LobRead(bool Found, object? Value);

/// <summary>Sizes of whole LOB values FerretSharp handles (characters for CLOB, bytes for BLOB).</summary>
public static class LobLimits
{
    /// <summary>Edited as text in the dialog up to this length (the user's decision); larger only via file.</summary>
    public const long MaxEditLength = 10 * 1024 * 1024;

    /// <summary>Loaded at all (to save to a file or compare) up to this length.</summary>
    public const long MaxLoadLength = 100 * 1024 * 1024;
}
