using FerretSharp.Core.Connections;
using FerretSharp.Core.Data;
using FerretSharp.Core.Query;
using FerretSharp.Core.Schema;

namespace FerretSharp.UI.Tests;

/// <summary>
/// A workspace session's editor without a database: writes write nothing but become actions of a <see cref="WriteLog"/>,
/// so undo up to an action and redo behave as with Oracle (WP-30). The transaction is always open, like the mock before.
/// </summary>
internal sealed class FakeDataEditor(Func<Func<Task>?> onCommit) : IDataEditor
{
    private readonly WriteLog _log = new();
    private int _savepoints;

    public TransactionInfo Transaction { get; } = new(TransactionMode.ReadWrite, DateTimeOffset.Now);

    public IReadOnlyList<WriteAction> Actions => _log.Actions;

    public IReadOnlyList<WriteAction> Undone => _log.Undone;

    /// <summary>The rows the next statement reports (to let a redo find other data).</summary>
    public int StatementRows { get; set; } = 1;

    /// <summary>The options of the last flush.</summary>
    public FlushOptions? LastFlush { get; private set; }

    public Task<FlushResult> FlushAsync(TableDetails table, IReadOnlyList<PendingOperation> operations, FlushOptions options, CancellationToken cancellationToken)
    {
        LastFlush = options;
        if (operations.Count == 0)
        {
            return Task.FromResult(FlushResult.Empty);
        }

        var redone = options.RedoOf is { } id ? _log.CheckRedo(id) : null;
        var action = new WriteAction(Guid.NewGuid(), WriteActionKind.Grid, $"{table.Table.Name}: {operations.Count}", operations.Count, DateTimeOffset.Now)
        {
            Statements = operations.Select(o => new QuerySpec($"{o.Kind} {table.Table.Name}", [])).ToList(),
            RedoOf = redone?.Origin,
        };
        _log.Add(action, Savepoint());
        var keys = operations
            .Where(o => o.Kind == OperationKind.Insert)
            .ToDictionary(o => o.Change.Id, RowKey (_) => new RowKey.RowId("ROW" + _savepoints));
        return Task.FromResult(new FlushResult(keys, new Dictionary<Guid, RowData>()) { Action = action });
    }

    public Task<WriteAction?> UndoLastAsync(Guid? expected, CancellationToken cancellationToken)
    {
        if (_log.Actions is not [.., var last])
        {
            return Task.FromResult<WriteAction?>(null);
        }

        _log.SavepointFor(last.Id, expected ?? last.Id);
        _log.TakeBack(last.Id);
        return Task.FromResult<WriteAction?>(last);
    }

    public Task<IReadOnlyList<WriteAction>> UndoToAsync(Guid actionId, Guid newest, CancellationToken cancellationToken)
    {
        _log.SavepointFor(actionId, newest);
        return Task.FromResult(_log.TakeBack(actionId));
    }

    public Task<WriteAction> RedoAsync(Guid actionId, CancellationToken cancellationToken)
    {
        var redone = _log.CheckRedo(actionId);
        return Task.FromResult(Execute(redone.Statements.Single(), redone));
    }

    public async Task CommitAsync(CancellationToken cancellationToken)
    {
        await (onCommit()?.Invoke() ?? Task.CompletedTask);
        _log.Clear();
    }

    public Task RollbackAsync(CancellationToken cancellationToken)
    {
        _log.Clear();
        return Task.CompletedTask;
    }

    public Task<WriteAction> ExecuteAsync(QuerySpec statement, CancellationToken cancellationToken) => Task.FromResult(Execute(statement, null));

    /// <summary>The DDL statements run, in order (WP-22).</summary>
    public List<string> Ddl { get; } = [];

    public Task ExecuteDdlAsync(string statement, CancellationToken cancellationToken)
    {
        Ddl.Add(statement);
        _log.Clear();
        return Task.CompletedTask;
    }

    private WriteAction Execute(QuerySpec statement, WriteAction? redone)
    {
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
