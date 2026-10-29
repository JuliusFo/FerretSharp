using FerretSharp.Core.Connections;
using FerretSharp.Core.Data;
using FerretSharp.Core.Oracle;
using FerretSharp.Core.Query;
using FerretSharp.Core.Resources;
using FerretSharp.Core.Schema;

namespace FerretSharp.UI.Tests;

/// <summary>
/// A workspace session's editor without a database: writes write nothing but become actions of a <see cref="WriteLog"/>,
/// so undo up to an action and redo behave as with Oracle (WP-30). The transaction is always open, like the mock before.
/// A locked session (<see cref="ReadOnly"/>, Prod by default) refuses to write as <c>OracleDataEditor</c> and Oracle do.
/// One call at a time, like <c>OracleDataEditor</c>: the calls come from pool threads (no UI dispatcher in the tests).
/// Review 3.40.0, C1: statements go through the session's own guards (<see cref="StatementGuard"/>), and DDL is refused
/// while something is written and neither committed nor rolled back – as the session does (<c>DdlOnlyWithoutTransaction</c>),
/// so a forgotten rollback before DDL no longer stays green.
/// </summary>
internal sealed class FakeDataEditor(Func<Func<Task>?> onCommit, Func<string, Exception?>? onDdl = null, Func<Func<Task>?>? onExecute = null) : IDataEditor
{
    private readonly Lock _gate = new();
    private readonly WriteLog _log = new();
    private readonly List<string> _ddl = [];
    private int _savepoints;

    /// <summary>Something was written since the last commit or rollback: the real session has its transaction open.</summary>
    private bool _written;

    private readonly TransactionInfo _transaction = new(TransactionMode.ReadWrite, DateTimeOffset.Now);
    private readonly TransactionInfo _readOnly = new(TransactionMode.ReadOnly, DateTimeOffset.Now);

    /// <summary>The transaction ended without commit behind FerretSharp's back (a rollback that failed with the connection).</summary>
    public bool Ended { get; set; }

    /// <summary>
    /// The session is locked (WP-08): in the read-only transaction of <c>UseReadOnlySnapshotsAsync</c> until
    /// <c>StopReadOnlySnapshotsAsync</c> (TestApp's connection switches it).
    /// </summary>
    public bool ReadOnly { get; set; }

    public TransactionInfo Transaction => Ended ? TransactionInfo.None : ReadOnly ? _readOnly : _transaction;

    public IReadOnlyList<WriteAction> Actions => Locked(() => (IReadOnlyList<WriteAction>)[.. _log.Actions]);

    public IReadOnlyList<WriteAction> Undone => Locked(() => (IReadOnlyList<WriteAction>)[.. _log.Undone]);

    /// <summary>The rows the next statement reports (to let a redo find other data).</summary>
    public int StatementRows { get; set; } = 1;

    /// <summary>The options of the last flush.</summary>
    public FlushOptions? LastFlush { get; private set; }

    /// <summary>A flush fails with this (a lock or concurrency conflict for the problem dialog); null writes.</summary>
    public Func<IReadOnlyList<PendingOperation>, FlushException?>? FailFlush { get; set; }

    /// <summary>A rollback fails with this (the connection is gone); null rolls back.</summary>
    public Exception? FailRollback { get; set; }

    /// <summary>The DDL statements run, in order (WP-22).</summary>
    public IReadOnlyList<string> Ddl => Locked(() => (IReadOnlyList<string>)[.. _ddl]);

    public Task<FlushResult> FlushAsync(TableDetails table, IReadOnlyList<PendingOperation> operations, FlushOptions options, CancellationToken cancellationToken)
    {
        LastFlush = options;
        if (ReadOnly && operations.Count > 0)
        {
            // OracleDataEditor does not check; Oracle refuses DML in the read-only transaction.
            return Task.FromException<FlushResult>(new DatabaseException("ORA-01456: may not perform insert/delete/update operation inside a READ ONLY transaction", "ORA-01456"));
        }

        if (FailFlush?.Invoke(operations) is { } failure)
        {
            return Task.FromException<FlushResult>(failure);
        }

        if (operations.Count == 0)
        {
            return Task.FromResult(FlushResult.Empty);
        }

        return Task.FromResult(Locked(() =>
        {
            var redone = options.RedoOf is { } id ? _log.CheckRedo(id) : null;
            var action = new WriteAction(Guid.NewGuid(), WriteActionKind.Grid, $"{table.Table.Name}: {operations.Count}", operations.Count, DateTimeOffset.Now)
            {
                Statements = operations.Select(o => new QuerySpec($"{o.Kind} {table.Table.Name}", [])).ToList(),
                RedoOf = redone?.Origin,
            };
            _log.Add(action, Savepoint());
            _written = true;
            var keys = operations
                .Where(o => o.Kind == OperationKind.Insert)
                .ToDictionary(o => o.Change.Id, RowKey (_) => new RowKey.RowId("ROW" + _savepoints));
            return new FlushResult(keys) { Action = action };
        }));
    }

    public Task<WriteAction?> UndoLastAsync(Guid? expected, CancellationToken cancellationToken) => Task.FromResult(Locked(() =>
    {
        if (_log.Actions is not [.., var last])
        {
            return null;
        }

        _log.SavepointFor(last.Id, expected ?? last.Id);
        _log.TakeBack(last.Id);
        return (WriteAction?)last;
    }));

    public Task<IReadOnlyList<WriteAction>> UndoToAsync(Guid actionId, Guid newest, CancellationToken cancellationToken) => Task.FromResult(Locked(() =>
    {
        _log.SavepointFor(actionId, newest);
        return _log.TakeBack(actionId);
    }));

    public Task<WriteAction> RedoAsync(Guid actionId, CancellationToken cancellationToken) => Task.FromResult(Locked(() =>
    {
        var redone = _log.CheckRedo(actionId);
        return redone.Copy is { } copy ? Copy(copy, redone) : Execute(redone.Statements.Single(), redone);
    }));

    /// <summary>The copies written, in order (WP-42).</summary>
    public IReadOnlyList<CopyWrite> Copies => Locked(() => (IReadOnlyList<CopyWrite>)[.. _copies]);

    private readonly List<CopyWrite> _copies = [];

    /// <summary>A copy fails with this (a constraint in the target); null writes.</summary>
    public Func<CopyWrite, Exception?>? FailCopy { get; set; }

    public Task<WriteAction> CopyAsync(CopyWrite copy, CancellationToken cancellationToken)
    {
        if (ReadOnly)
        {
            return Task.FromException<WriteAction>(new RefusedException(OracleText.WorkspaceReadOnly));
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<WriteAction>(cancellationToken);
        }

        return FailCopy?.Invoke(copy) is { } failure
            ? Task.FromException<WriteAction>(failure)
            : Task.FromResult(Locked(() => Copy(copy, null)));
    }

    /// <summary>Under <see cref="_gate"/>.</summary>
    private WriteAction Copy(CopyWrite copy, WriteAction? redone)
    {
        _written = true;
        _copies.Add(copy);
        var action = new WriteAction(Guid.NewGuid(), WriteActionKind.Copy, copy.Description, copy.Rows, DateTimeOffset.Now)
        {
            Statements = [.. copy.Steps.OfType<CopyInsert>().SelectMany(i => i.Batch.Expand())],
            RedoOf = redone?.Origin,
            Copy = copy,
        };
        _log.Add(action, Savepoint());
        return action;
    }

    public async Task CommitAsync(CancellationToken cancellationToken)
    {
        await (onCommit()?.Invoke() ?? Task.CompletedTask);
        lock (_gate)
        {
            _log.Clear();
            _written = false;
        }
    }

    public Task RollbackAsync(CancellationToken cancellationToken)
    {
        if (FailRollback is { } failure)
        {
            return Task.FromException(failure);
        }

        lock (_gate)
        {
            _log.Clear();
            _written = false;
        }

        return Task.CompletedTask;
    }

    public async Task<WriteAction> ExecuteAsync(QuerySpec statement, CancellationToken cancellationToken)
    {
        if (!StatementGuard.IsWrite(statement.Sql))
        {
            throw new InvalidOperationException("Writes only take a single INSERT, UPDATE, DELETE or MERGE statement.");
        }

        if (ReadOnly)
        {
            throw new RefusedException(OracleText.WorkspaceReadOnly);
        }

        await (onExecute?.Invoke()?.Invoke() ?? Task.CompletedTask);
        return Locked(() => Execute(statement, null));
    }

    public Task ExecuteDdlAsync(string statement, CancellationToken cancellationToken)
    {
        if (!StatementGuard.IsDdl(statement))
        {
            throw new InvalidOperationException("The schema path only takes a single DDL statement – no TRUNCATE, PL/SQL or bind variables.");
        }

        if (ReadOnly)
        {
            return Task.FromException(new RefusedException(OracleText.WorkspaceReadOnly));
        }

        if (Locked(() => _written))
        {
            return Task.FromException(new RefusedException(OracleText.DdlOnlyWithoutTransaction));
        }

        if (onDdl?.Invoke(statement) is { } failure)
        {
            return Task.FromException(failure);
        }

        lock (_gate)
        {
            _ddl.Add(statement);
            _log.Clear();
        }

        return Task.CompletedTask;
    }

    private T Locked<T>(Func<T> body)
    {
        lock (_gate)
        {
            return body();
        }
    }

    /// <summary>Under <see cref="_gate"/>.</summary>
    private WriteAction Execute(QuerySpec statement, WriteAction? redone)
    {
        _written = true;
        var action = new WriteAction(Guid.NewGuid(), WriteActionKind.Statement, statement.Sql, StatementRows, DateTimeOffset.Now)
        {
            Statements = [statement],
            RedoOf = redone?.Origin,
        };
        _log.Add(action, Savepoint());
        return action;
    }

    private string Savepoint() => "SP" + ++_savepoints;
}
