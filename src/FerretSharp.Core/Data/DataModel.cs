using FerretSharp.Core.Query;
using FerretSharp.Core.Schema;

namespace FerretSharp.Core.Data;

/// <summary>Identifies a row for navigation (v1) and later for UPDATE/DELETE (v2). See CLAUDE.md 5.5.</summary>
public abstract record RowKey
{
    /// <summary>Compares by value: two keys with the same column values are equal.</summary>
    public sealed record PrimaryKey(IReadOnlyList<object?> Values) : RowKey
    {
        public bool Equals(PrimaryKey? other) => other is not null && Values.SequenceEqual(other.Values);

        public override int GetHashCode()
        {
            var hash = new HashCode();
            foreach (var value in Values)
            {
                hash.Add(value);
            }

            return hash.ToHashCode();
        }
    }

    public sealed record RowId(string Value) : RowKey;

    public sealed record None : RowKey
    {
        public static readonly None Instance = new();
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

public interface IDataAccess
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

    /// <summary>Rows fetched for the actual plan unless the whole result is wanted: one grid page.</summary>
    public const int ActualPlanPageSize = 500;
}

/// <summary>A result column of a free query; <see cref="Column"/> is derived from the driver's type, for formatting.</summary>
public sealed record SqlColumn(string Name, ColumnInfo Column);

/// <param name="Rows">Values in <paramref name="Columns"/> order (LOBs whole, <see cref="BigNumber"/> beyond 28 digits).</param>
/// <param name="DataAsOf">Start of the read-only snapshot (locked workspaces); null otherwise.</param>
public sealed record SqlPage(
    IReadOnlyList<SqlColumn> Columns, IReadOnlyList<IReadOnlyList<object?>> Rows, bool IsLastPage, TimeSpan Elapsed, DateTimeOffset? DataAsOf = null);

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
