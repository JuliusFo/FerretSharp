using FerretSharp.Core.Connections;
using FerretSharp.Core.Data;
using FerretSharp.Core.Query;
using FerretSharp.Core.Resources;
using FerretSharp.Core.Schema;
using Oracle.ManagedDataAccess.Client;

namespace FerretSharp.Core.Oracle;

/// <summary>
/// Writes pending changes in the workspace session's transaction (docs/architecture.md 1.6): savepoint per flush, then per row
/// <c>SELECT … FOR UPDATE WAIT n</c> (lock + current values for the concurrency check), then the DML. Any failure
/// rolls the flush back to its savepoint, so a flush is all or nothing.
/// </summary>
internal sealed class OracleDataEditor(OracleSession session) : IDataEditor
{
    /// <summary>The writes of the open transaction with their savepoints, and the ones taken back (redo, WP-30).</summary>
    private readonly WriteLog _log = new();
    private int _counter;

    /// <summary>
    /// One write at a time – flush, statement, undo, commit, rollback. Each takes several turns at the session (begin,
    /// savepoint, DML, then the action list): a grid flush and a statement of the SQL editor running at once could both
    /// begin a transaction or lose an action, and an undo could take back a write that was still running.
    /// </summary>
    private readonly SemaphoreSlim _writing = new(1, 1);

    public TransactionInfo Transaction => session.Transaction;

    /// <summary>Only while a writing transaction is open: one that ended elsewhere (lost connection) took them along.</summary>
    public IReadOnlyList<WriteAction> Actions => IsWriting ? _log.Actions : [];

    /// <summary>Like <see cref="Actions"/>: only within the transaction they were taken back in.</summary>
    public IReadOnlyList<WriteAction> Undone => IsWriting ? _log.Undone : [];

    private bool IsWriting => session.Transaction.Mode == TransactionMode.ReadWrite;

    public Task<FlushResult> FlushAsync(
        TableDetails table, IReadOnlyList<PendingOperation> operations, FlushOptions options, CancellationToken cancellationToken) =>
        operations.Count == 0
            ? Task.FromResult(FlushResult.Empty)
            : OneAtATimeAsync(() => FlushCoreAsync(table, operations, options, cancellationToken), cancellationToken);

    private async Task<FlushResult> FlushCoreAsync(
        TableDetails table, IReadOnlyList<PendingOperation> operations, FlushOptions options, CancellationToken cancellationToken)
    {
        var redone = options.RedoOf is { } redoOf ? CheckRedo(redoOf, WriteActionKind.Grid) : null;
        await BeginIfNeededAsync(cancellationToken);
        var savepoint = NextSavepoint("FS_FLUSH_");
        await session.SavepointAsync(savepoint, cancellationToken);

        var newKeys = new Dictionary<Guid, RowKey>();
        var inserted = new Dictionary<Guid, RowData>();
        var statements = new List<QuerySpec>();
        foreach (var operation in operations)
        {
            try
            {
                statements.Add(await ApplyAsync(table, operation, options, newKeys, inserted, cancellationToken));
            }
            catch (Exception ex)
            {
                // Whatever failed – Oracle, a value that cannot be read back, cancellation –, nothing of this flush may stay.
                await RollBackFlushAsync(savepoint);
                if (ex is DatabaseException { IsConnectionLost: false } error)
                {
                    throw error.IsAny(OracleErrorCodes.RowLocked)
                        ? new LockConflictException(operation, error)
                        : new WriteFailedException(operation, error);
                }

                throw;
            }
        }

        var action = new WriteAction(Guid.NewGuid(), WriteActionKind.Grid, DescribeFlush(table, operations), operations.Count, DateTimeOffset.Now)
        {
            Statements = statements,
            RedoOf = redone?.Origin,
        };
        _log.Add(action, savepoint);
        return new FlushResult(newKeys, inserted) { Action = action };
    }

    /// <summary>
    /// Takes back the newest action, whoever wrote it: a savepoint cannot be rolled back selectively – everything after
    /// it goes, so undo always takes the last action and never reaches past a statement to an older grid write.
    /// </summary>
    public Task<WriteAction?> UndoLastAsync(Guid? expected, CancellationToken cancellationToken) =>
        OneAtATimeAsync(async () =>
        {
            if (Actions is not [.., var last])
            {
                return null;
            }

            if (expected is { } id && last.Id != id)
            {
                // Written meanwhile (a statement that was still running): ↶ would take back something the user did not pick.
                throw new RefusedException(TextFormat.Format(OracleText.UndoWrittenMeanwhile, last.Display));
            }

            await session.RollbackToSavepointAsync(_log.SavepointFor(last.Id, last.Id), cancellationToken);
            _log.TakeBack(last.Id);
            return (WriteAction?)last;
        }, cancellationToken);

    public Task<IReadOnlyList<WriteAction>> UndoToAsync(Guid actionId, Guid newest, CancellationToken cancellationToken) =>
        OneAtATimeAsync(async () =>
        {
            if (!IsWriting)
            {
                throw new RefusedException(DataText.ActionNoLongerOpen);
            }

            await session.RollbackToSavepointAsync(_log.SavepointFor(actionId, newest), cancellationToken);
            return _log.TakeBack(actionId);
        }, cancellationToken);

    public Task<WriteAction> RedoAsync(Guid actionId, CancellationToken cancellationToken) =>
        OneAtATimeAsync(() =>
        {
            var redone = CheckRedo(actionId, WriteActionKind.Statement);
            return ExecuteCoreAsync(redone.Statements.Single(), redone, cancellationToken);
        }, cancellationToken);

    /// <summary>The write to apply again: the next redo of the open transaction, of the expected kind.</summary>
    private WriteAction CheckRedo(Guid actionId, WriteActionKind kind)
    {
        if (!IsWriting)
        {
            throw new RefusedException(DataText.RedoNotPossible);
        }

        var redone = _log.CheckRedo(actionId);
        return redone.Kind == kind
            ? redone
            : throw new InvalidOperationException($"{redone.Kind} actions are not redone by a {kind} write.");
    }

    public Task CommitAsync(CancellationToken cancellationToken) =>
        OneAtATimeAsync(async () =>
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
                    _log.Clear();
                }
            }

            return true;
        }, cancellationToken);

    public Task RollbackAsync(CancellationToken cancellationToken) =>
        OneAtATimeAsync(async () =>
        {
            await session.RollbackAsync(cancellationToken);
            _log.Clear();
            return true;
        }, cancellationToken);

    public Task<WriteAction> ExecuteAsync(QuerySpec statement, CancellationToken cancellationToken) =>
        OneAtATimeAsync(() => ExecuteCoreAsync(statement, redone: null, cancellationToken), cancellationToken);

    private async Task<T> OneAtATimeAsync<T>(Func<Task<T>> write, CancellationToken cancellationToken)
    {
        await _writing.WaitAsync(cancellationToken);
        try
        {
            return await write();
        }
        finally
        {
            _writing.Release();
        }
    }

    private string NextSavepoint(string prefix) =>
        prefix + Interlocked.Increment(ref _counter).ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <param name="redone">The write a redo applies again; null for a new statement.</param>
    private async Task<WriteAction> ExecuteCoreAsync(QuerySpec statement, WriteAction? redone, CancellationToken cancellationToken)
    {
        if (session.Transaction.Mode == TransactionMode.ReadOnly)
        {
            // Oracle would refuse as well (ORA-01456); this way the message names the reason.
            throw new RefusedException(OracleText.WorkspaceReadOnly);
        }

        await BeginIfNeededAsync(cancellationToken);
        var savepoint = NextSavepoint("FS_EXEC_");
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

        var action = new WriteAction(Guid.NewGuid(), WriteActionKind.Statement, DescribeStatement(statement.Sql), rows, DateTimeOffset.Now)
        {
            Statements = [statement],
            RedoOf = redone?.Origin,
        };
        _log.Add(action, savepoint);
        return action;
    }

    /// <summary>A new transaction starts without actions (one that ended elsewhere may have left some behind).</summary>
    private async Task BeginIfNeededAsync(CancellationToken cancellationToken)
    {
        if (session.Transaction.Mode == TransactionMode.None)
        {
            _log.Clear();
            await session.BeginTransactionAsync(cancellationToken);
        }
    }

    /// <summary>"KUNDEN: 2 changed, 1 new, 1 deleted".</summary>
    internal static string DescribeFlush(TableDetails table, IReadOnlyList<PendingOperation> operations)
    {
        var parts = new[] { (OperationKind.Update, OracleText.FlushChanged), (OperationKind.Insert, OracleText.FlushInserted), (OperationKind.Delete, OracleText.FlushDeleted) }
            .Select(p => (Count: operations.Count(o => o.Kind == p.Item1), Text: p.Item2))
            .Where(p => p.Count > 0)
            .Select(p => TextFormat.Format(p.Text, p.Count));
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
    /// gone): that error is reported instead – a <see cref="DatabaseException"/> from the session, so a lost connection is noticed.
    /// </summary>
    private Task RollBackFlushAsync(string savepoint) => session.RollbackToSavepointAsync(savepoint, CancellationToken.None);

    /// <returns>The DML that ran (for <see cref="WriteAction.Statements"/>).</returns>
    private async Task<QuerySpec> ApplyAsync(
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
                return delete;
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
                return update;
            }

            case OperationKind.Insert:
            {
                var insert = DmlBuilder.Insert(table, operation.Values);
                var result = await session.ExecuteNonQueryAsync(insert.Sql, insert.Parameters, cancellationToken);
                var rowId = result.Outputs.GetValueOrDefault(DmlBuilder.RowIdOutput) as string
                            ?? throw new RefusedException(OracleText.NoRowIdReturned);

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

                return insert;
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(operation), operation.Kind, null);
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
