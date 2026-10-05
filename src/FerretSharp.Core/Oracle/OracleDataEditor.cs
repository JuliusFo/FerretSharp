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

    private readonly Stack<string> _savepoints = new();
    private int _counter;

    public TransactionInfo Transaction => session.Transaction;

    public int FlushCount => _savepoints.Count;

    public async Task<FlushResult> FlushAsync(
        TableDetails table, IReadOnlyList<PendingOperation> operations, FlushOptions options, CancellationToken cancellationToken)
    {
        if (operations.Count == 0)
        {
            return FlushResult.Empty;
        }

        await OracleErrors.Guard(async () =>
        {
            if (session.Transaction.Mode == TransactionMode.None)
            {
                await session.BeginTransactionAsync(cancellationToken);
            }

            return true;
        });

        var savepoint = "FS_FLUSH_" + (++_counter).ToString(System.Globalization.CultureInfo.InvariantCulture);
        await OracleErrors.Guard(async () =>
        {
            await session.SavepointAsync(savepoint, cancellationToken);
            return true;
        });

        var newKeys = new Dictionary<Guid, RowKey>();
        var inserted = new Dictionary<Guid, RowData>();
        foreach (var operation in operations)
        {
            try
            {
                await ApplyAsync(table, operation, options, newKeys, inserted, cancellationToken);
            }
            catch (Exception ex) when (ex is FlushException or OperationCanceledException or OracleStatementException or InvalidOperationException)
            {
                await session.RollbackToSavepointAsync(savepoint, CancellationToken.None);
                if (ex is OracleStatementException statement && OracleErrors.Translate(statement) is { } error)
                {
                    throw LockErrors.Contains(error.ErrorCode ?? "")
                        ? new LockConflictException(operation, error)
                        : new WriteFailedException(operation, error);
                }

                throw;
            }
        }

        _savepoints.Push(savepoint);
        return new FlushResult(newKeys, inserted);
    }

    public Task UndoLastFlushAsync(CancellationToken cancellationToken) =>
        OracleErrors.Guard(async () =>
        {
            if (!_savepoints.TryPeek(out var savepoint))
            {
                throw new InvalidOperationException("Es gibt keinen Schreibvorgang, der sich zurücknehmen lässt.");
            }

            await session.RollbackToSavepointAsync(savepoint, cancellationToken);
            _savepoints.Pop();
            return true;
        });

    public Task CommitAsync(CancellationToken cancellationToken) =>
        OracleErrors.Guard(async () =>
        {
            await session.CommitAsync(cancellationToken);
            _savepoints.Clear();
            return true;
        });

    public Task RollbackAsync(CancellationToken cancellationToken) =>
        OracleErrors.Guard(async () =>
        {
            await session.RollbackAsync(cancellationToken);
            _savepoints.Clear();
            return true;
        });

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
                            ?? throw new InvalidOperationException("Oracle hat keine ROWID für die neue Zeile geliefert.");

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
