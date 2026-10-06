using FerretSharp.Core.Connections;
using FerretSharp.Core.Data;
using FerretSharp.Core.Query;
using FerretSharp.Core.Schema;
using Oracle.ManagedDataAccess.Client;

namespace FerretSharp.Core.Oracle;

/// <summary>
/// Writes pending changes in the workspace session's transaction (CLAUDE.md 5.6): savepoint per flush, then per row
/// <c>SELECT … FOR UPDATE WAIT n</c> (lock + current values for the concurrency check), then the DML. Any failure
/// rolls the flush back to its savepoint, so a flush is all or nothing.
/// </summary>
internal sealed class OracleDataEditor(OracleSession session) : IDataEditor
{
    /// <summary>ORA-30006: resource busy, WAIT timeout expired; ORA-00054: resource busy (NOWAIT).</summary>
    private static readonly HashSet<string> LockErrors = ["ORA-30006", "ORA-00054"];

    /// <summary>The writes of the open transaction with their savepoints, oldest first; replaced, never changed (read by the UI thread).</summary>
    private volatile Entry[] _actions = [];
    private int _counter;

    private sealed record Entry(WriteAction Action, string Savepoint);

    public TransactionInfo Transaction => session.Transaction;

    /// <summary>Only while a writing transaction is open: one that ended elsewhere (lost connection) took them along.</summary>
    public IReadOnlyList<WriteAction> Actions =>
        session.Transaction.Mode == TransactionMode.ReadWrite ? _actions.Select(e => e.Action).ToList() : [];

    public async Task<FlushResult> FlushAsync(
        TableDetails table, IReadOnlyList<PendingOperation> operations, FlushOptions options, CancellationToken cancellationToken)
    {
        if (operations.Count == 0)
        {
            return FlushResult.Empty;
        }

        await OracleErrors.Guard(() => BeginIfNeededAsync(cancellationToken));

        var savepoint = "FS_FLUSH_" + (++_counter).ToString(System.Globalization.CultureInfo.InvariantCulture);
        await OracleErrors.Guard(() => session.SavepointAsync(savepoint, cancellationToken));

        var newKeys = new Dictionary<Guid, RowKey>();
        var inserted = new Dictionary<Guid, RowData>();
        foreach (var operation in operations)
        {
            try
            {
                await ApplyAsync(table, operation, options, newKeys, inserted, cancellationToken);
            }
            catch (Exception ex)
            {
                // Whatever failed – Oracle, a value that cannot be read back, cancellation –, nothing of this flush may stay.
                await RollBackFlushAsync(savepoint);
                if (ex is OracleStatementException statement && OracleErrors.Translate(statement) is { } error)
                {
                    throw LockErrors.Contains(error.ErrorCode ?? "")
                        ? new LockConflictException(operation, error)
                        : new WriteFailedException(operation, error);
                }

                throw;
            }
        }

        var action = new WriteAction(Guid.NewGuid(), WriteActionKind.Grid, DescribeFlush(table, operations), operations.Count, DateTimeOffset.Now);
        Add(action, savepoint);
        return new FlushResult(newKeys, inserted) { Action = action };
    }

    /// <summary>
    /// Takes back the newest action, whoever wrote it: a savepoint cannot be rolled back selectively – everything after
    /// it goes, so undo always takes the last action and never reaches past a statement to an older grid write.
    /// </summary>
    public Task<WriteAction?> UndoLastAsync(CancellationToken cancellationToken) =>
        OracleErrors.Guard(async () =>
        {
            if (Actions.Count == 0 || _actions is not [.., var last])
            {
                return null;
            }

            await session.RollbackToSavepointAsync(last.Savepoint, cancellationToken);
            _actions = _actions[..^1];
            return (WriteAction?)last.Action;
        });

    public Task CommitAsync(CancellationToken cancellationToken) =>
        OracleErrors.Guard(async () =>
        {
            try
            {
                await session.CommitAsync(cancellationToken);
            }
            finally
            {
                // A failed commit can end the transaction as well (ORA-02091); its savepoints are gone then.
                if (session.Transaction.Mode == TransactionMode.None)
                {
                    _actions = [];
                }
            }
        });

    public Task RollbackAsync(CancellationToken cancellationToken) =>
        OracleErrors.Guard(async () =>
        {
            await session.RollbackAsync(cancellationToken);
            _actions = [];
        });

    public Task<WriteAction> ExecuteAsync(QuerySpec statement, CancellationToken cancellationToken) =>
        OracleErrors.Guard(async () =>
        {
            if (session.Transaction.Mode == TransactionMode.ReadOnly)
            {
                // Oracle would refuse as well (ORA-01456); this way the message names the reason.
                throw new RefusedException("Der Workspace ist schreibgeschützt – erst freischalten, dann schreiben.");
            }

            await BeginIfNeededAsync(cancellationToken);
            var savepoint = "FS_EXEC_" + (++_counter).ToString(System.Globalization.CultureInfo.InvariantCulture);
            await session.SavepointAsync(savepoint, cancellationToken);
            int rows;
            try
            {
                rows = (await session.ExecuteNonQueryAsync(statement.Sql, statement.Parameters, cancellationToken)).Rows;
            }
            catch
            {
                await RollBackFlushAsync(savepoint);
                throw;
            }

            var action = new WriteAction(Guid.NewGuid(), WriteActionKind.Statement, DescribeStatement(statement.Sql), rows, DateTimeOffset.Now);
            Add(action, savepoint);
            return action;
        });

    /// <summary>A new transaction starts without actions (one that ended elsewhere may have left some behind).</summary>
    private async Task BeginIfNeededAsync(CancellationToken cancellationToken)
    {
        if (session.Transaction.Mode == TransactionMode.None)
        {
            _actions = [];
            await session.BeginTransactionAsync(cancellationToken);
        }
    }

    private void Add(WriteAction action, string savepoint) => _actions = [.. _actions, new Entry(action, savepoint)];

    /// <summary>"KUNDEN: 2 geändert, 1 neu, 1 gelöscht".</summary>
    internal static string DescribeFlush(TableDetails table, IReadOnlyList<PendingOperation> operations)
    {
        var parts = new[] { (OperationKind.Update, "geändert"), (OperationKind.Insert, "neu"), (OperationKind.Delete, "gelöscht") }
            .Select(p => (Count: operations.Count(o => o.Kind == p.Item1), Text: p.Item2))
            .Where(p => p.Count > 0)
            .Select(p => $"{p.Count} {p.Text}");
        return $"{table.Table.DisplayName}: {string.Join(", ", parts)}";
    }

    /// <summary>"UPDATE AUFTRAG": the statement's kind and the table it writes.</summary>
    internal static string DescribeStatement(string sql)
    {
        var info = SqlScript.Analyze(sql);
        return info.Tables.FirstOrDefault(t => t.Depth == 0) is { } table ? $"{info.FirstWord} {table.Name}" : info.FirstWord;
    }

    /// <summary>
    /// Back to the savepoint after a failed write. If even that fails, the session is in doubt (usually the connection is
    /// gone): that error is reported instead – as <see cref="DatabaseException"/>, so a lost connection is noticed.
    /// </summary>
    private async Task RollBackFlushAsync(string savepoint)
    {
        try
        {
            await session.RollbackToSavepointAsync(savepoint, CancellationToken.None);
        }
        catch (Exception ex) when (OracleErrors.Translate(ex) is { } translated)
        {
            throw translated;
        }
    }

    private async Task ApplyAsync(
        TableDetails table, PendingOperation operation, FlushOptions options,
        Dictionary<Guid, RowKey> newKeys, Dictionary<Guid, RowData> inserted, CancellationToken cancellationToken)
    {
        switch (operation.Kind)
        {
            case OperationKind.Delete:
            {
                _ = await LockAsync(table, operation, [], options.LockWaitSeconds, cancellationToken) ?? throw new RowGoneException(operation);
                var delete = DmlBuilder.Delete(table, operation.Key);
                await session.ExecuteNonQueryAsync(delete.Sql, delete.Parameters, cancellationToken);
                break;
            }

            case OperationKind.Update:
            {
                var columns = operation.Values.Keys.Order().ToList();
                var current = await LockAsync(table, operation, columns, options.LockWaitSeconds, cancellationToken) ?? throw new RowGoneException(operation);
                if (options.Overwrite?.Contains(operation.Change.Id) != true)
                {
                    var differences = columns
                        .Select((column, i) => (column, actual: current[i]))
                        .Where(c => operation.Expected.TryGetValue(c.column, out var expected) && !OracleTypeMapper.ValuesEqual(expected, c.actual))
                        .Select(c => new ConcurrencyDifference(c.column, operation.Expected[c.column], c.actual))
                        .ToList();
                    if (differences.Count > 0)
                    {
                        throw new ConcurrencyConflictException(operation, differences);
                    }
                }

                var update = DmlBuilder.Update(table, operation.Key, operation.Values);
                await session.ExecuteNonQueryAsync(update.Sql, update.Parameters, cancellationToken);
                break;
            }

            case OperationKind.Insert:
            {
                var insert = DmlBuilder.Insert(table, operation.Values);
                var result = await session.ExecuteNonQueryAsync(insert.Sql, insert.Parameters, cancellationToken);
                var rowId = result.Outputs.GetValueOrDefault(DmlBuilder.RowIdOutput) as string
                            ?? throw new RefusedException("Oracle hat keine ROWID für die neue Zeile geliefert.");

                // Reload: defaults, identity values and triggers decide what was stored – and the row's real key.
                var query = QueryBuilder.BuildSelectByRowId(table, rowId);
                var row = await session.ExecuteReaderAsync(query.Sql, query.Parameters, async (reader, ct) =>
                    await reader.ReadAsync(ct) ? OracleDataAccess.ReadRow((OracleDataReader)reader, query, table) : null, cancellationToken);
                if (row is not null)
                {
                    newKeys[operation.Change.Id] = row.Key;
                    inserted[operation.Change.Id] = row;
                }
                else
                {
                    newKeys[operation.Change.Id] = new RowKey.RowId(rowId);
                }

                break;
            }
        }
    }

    /// <summary>Current values of <paramref name="columns"/> with the row locked; null if the row is gone.</summary>
    private Task<object?[]?> LockAsync(
        TableDetails table, PendingOperation operation, IReadOnlyList<int> columns, int waitSeconds, CancellationToken cancellationToken)
    {
        var spec = DmlBuilder.Lock(table, operation.Key, columns, waitSeconds);
        return session.ExecuteLockingReaderAsync(spec.Sql, spec.Parameters, async (reader, ct) =>
        {
            if (!await reader.ReadAsync(ct))
            {
                return null;
            }

            var oracle = (OracleDataReader)reader;
            return columns
                .Select((column, i) => oracle.IsDBNull(i) ? null : OracleDataAccess.ReadValue(oracle, i, table.Columns[column]))
                .ToArray();
        }, cancellationToken);
    }
}
