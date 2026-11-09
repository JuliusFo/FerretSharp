using FerretSharp.Core.Audit;
using FerretSharp.Core.Data;
using FerretSharp.Core.Query;
using FerretSharp.Core.Schema;

namespace FerretSharp.Core.Connections;

/// <summary>
/// The read path of a <see cref="ReadSession"/> as an <see cref="IDataAccess"/> (time travel, WP-78): each call is a read of its
/// own – it opens the session if needed and keeps it from closing while it runs – and reads its table as it was at
/// <paramref name="asOf"/>. So a time travel tab hands it to everything that reads for a tab (grid, footer, export, copy,
/// form, LOB) like a workspace's data access, and nothing of it runs on the workspace's session.
/// </summary>
internal sealed class ReadSessionData(ReadSession reads, long? asOf) : IDataAccess
{
    private Task<T> ReadAsync<T>(Func<IDataAccess, CancellationToken, Task<T>> read, CancellationToken cancellationToken) =>
        reads.ReadAsync((data, ct) => read(asOf is { } scn ? data.AsOf(scn) : data, ct), cancellationToken);

    private Task ReadAsync(Func<IDataAccess, CancellationToken, Task> read, CancellationToken cancellationToken) =>
        ReadAsync(async (data, ct) =>
        {
            await read(data, ct);
            return true;
        }, cancellationToken);

    public IDataAccess AsOf(long scn) => new ReadSessionData(reads, scn);

    public Task<IReadOnlyList<LookupRow>> LookupAsync(
        TableDetails target, int keyColumn, IReadOnlyList<int> displayColumns, IReadOnlyList<object> keys, CancellationToken cancellationToken) =>
        ReadAsync((data, ct) => data.LookupAsync(target, keyColumn, displayColumns, keys, ct), cancellationToken);

    public Task<RowPage> ReadPageAsync(
        TableDetails table, IReadOnlyList<FilterCondition> filters, IReadOnlyList<SortSpec> sorts, PageSpec page, CancellationToken cancellationToken) =>
        ReadAsync((data, ct) => data.ReadPageAsync(table, filters, sorts, page, ct), cancellationToken);

    public Task<long> CountAsync(TableDetails table, IReadOnlyList<FilterCondition> filters, CancellationToken cancellationToken) =>
        ReadAsync((data, ct) => data.CountAsync(table, filters, ct), cancellationToken);

    public Task<LobRead> ReadLobAsync(TableDetails table, RowKey key, int column, CancellationToken cancellationToken) =>
        ReadAsync((data, ct) => data.ReadLobAsync(table, key, column, ct), cancellationToken);

    public Task<SqlPage> ReadSqlAsync(QuerySpec query, int skip, int take, CancellationToken cancellationToken) =>
        ReadAsync((data, ct) => data.ReadSqlAsync(query, skip, take, ct), cancellationToken);

    public Task<ExecutionPlan> ExplainActualAsync(QuerySpec query, bool wholeResult, CancellationToken cancellationToken) =>
        ReadAsync((data, ct) => data.ExplainActualAsync(query, wholeResult, ct), cancellationToken);

    public Task ReadAllAsync(
        TableDetails table, IReadOnlyList<FilterCondition> filters, IReadOnlyList<SortSpec> sorts, RowBlockHandler onBlock, CancellationToken cancellationToken) =>
        ReadAsync((data, ct) => data.ReadAllAsync(table, filters, sorts, onBlock, ct), cancellationToken);

    public Task ReadSqlAllAsync(QuerySpec query, IReadOnlyList<string> columns, RowBlockHandler onBlock, CancellationToken cancellationToken) =>
        ReadAsync((data, ct) => data.ReadSqlAllAsync(query, columns, onBlock, ct), cancellationToken);

    public Task ReadColumnAllAsync(
        TableDetails table, IReadOnlyList<int> columns, IReadOnlyList<FilterCondition> filters, IReadOnlyList<SortSpec> sorts, bool distinct,
        RowBlockHandler onBlock, CancellationToken cancellationToken) =>
        ReadAsync((data, ct) => data.ReadColumnAllAsync(table, columns, filters, sorts, distinct, onBlock, ct), cancellationToken);

    public Task<IReadOnlyList<RowData>> ReadByKeysAsync(
        TableDetails table, IReadOnlyList<int>? keyColumns, IReadOnlyList<IReadOnlyList<object>> keys, bool wholeLobs, int? maxRows,
        CancellationToken cancellationToken) =>
        ReadAsync((data, ct) => data.ReadByKeysAsync(table, keyColumns, keys, wholeLobs, maxRows, ct), cancellationToken);

    public Task<IReadOnlyList<AuditRowChange>> ReadAuditChangesAsync(
        TableDetails table, AuditVersions versions, IReadOnlyList<RowData> rows, Action? started, CancellationToken cancellationToken) =>
        ReadAsync((data, ct) => data.ReadAuditChangesAsync(table, versions, rows, started, ct), cancellationToken);

    public Task<DatabasePoint> CurrentPointAsync(bool exact, CancellationToken cancellationToken) =>
        ReadAsync((data, ct) => data.CurrentPointAsync(exact, ct), cancellationToken);

    public Task<DatabasePoint> PointBackAsync(TimeSpan back, CancellationToken cancellationToken) =>
        ReadAsync((data, ct) => data.PointBackAsync(back, ct), cancellationToken);

    public Task<TimeSpan?> UndoReachAsync(CancellationToken cancellationToken) =>
        ReadAsync((data, ct) => data.UndoReachAsync(ct), cancellationToken);

    public Task<DiffRead> ReadDiffAsync(TableDetails table, IReadOnlyList<FilterCondition> filters, long scn, CancellationToken cancellationToken) =>
        ReadAsync((data, ct) => data.ReadDiffAsync(table, filters, scn, ct), cancellationToken);
}
