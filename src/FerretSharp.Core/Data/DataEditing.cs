using FerretSharp.Core.Connections;
using FerretSharp.Core.Query;
using FerretSharp.Core.Resources;
using FerretSharp.Core.Schema;

namespace FerretSharp.Core.Data;

/// <param name="LockWaitSeconds">How long to wait for a row another session has locked (then <see cref="LockConflictException"/>).</param>
/// <param name="Overwrite">Changes (<see cref="RowChange.Id"/>) whose concurrency conflict the user chose to overwrite.</param>
/// <param name="RedoOf">
/// The flush applies a grid write again that was taken back (WP-30): it must be the next of <see cref="IDataEditor.Undone"/>,
/// refused otherwise. Null: a new write, which ends redo.
/// </param>
public sealed record FlushOptions(int LockWaitSeconds = DmlBuilder.DefaultLockWaitSeconds, IReadOnlySet<Guid>? Overwrite = null, Guid? RedoOf = null);

/// <param name="NewKeys">Key of each inserted row by <see cref="RowChange.Id"/>.</param>
/// <param name="InsertedRows">Inserted rows as Oracle stored them (defaults, identity values, triggers).</param>
public sealed record FlushResult(IReadOnlyDictionary<Guid, RowKey> NewKeys, IReadOnlyDictionary<Guid, RowData> InsertedRows)
{
    public static readonly FlushResult Empty = new(new Dictionary<Guid, RowKey>(), new Dictionary<Guid, RowData>());

    /// <summary>The flush as one of <see cref="IDataEditor.Actions"/>; null if there was nothing to write.</summary>
    public WriteAction? Action { get; init; }
}

public enum WriteActionKind
{
    /// <summary>Pending changes of a table tab written (Ctrl+S, before a commit).</summary>
    Grid,

    /// <summary>A statement of the SQL editor or the LINQ console (ExecuteUpdate/ExecuteDelete).</summary>
    Statement,
}

/// <summary>One write in a workspace's open transaction, as the status bar and the change overview list it.</summary>
/// <param name="Description">"CUSTOMERS: 2 changed, 1 new" or "UPDATE ORDERS".</param>
/// <param name="Rows">Rows written (grid: operations; statement: rows Oracle reported).</param>
public sealed record WriteAction(Guid Id, WriteActionKind Kind, string Description, int Rows, DateTimeOffset At)
{
    /// <summary>
    /// What ran, with the bind values (WP-30): the DML of a grid write (without the locking SELECTs), the statement of the
    /// SQL editor or the LINQ console. Redo runs a statement again from here. Shown in the UI only – never log the values
    /// (Prod); <see cref="ToString"/> leaves them out.
    /// </summary>
    public IReadOnlyList<QuerySpec> Statements { get; init; } = [];

    /// <summary>The write this one applies again (redo): the first of its line, so a write redone twice still names it.</summary>
    public Guid? RedoOf { get; init; }

    /// <summary>The first write of the line: <see cref="Id"/>, or for a redo the write it applies again.</summary>
    public Guid Origin => RedoOf ?? Id;

    /// <summary>Without <see cref="Statements"/>: their bind values must not reach a log.</summary>
    public override string ToString() => $"{Kind} {Description} ({Rows}, {At:HH:mm:ss})";

    /// <summary>"UPDATE ORDERS · 12 rows"; a grid write counts its rows in the description already.</summary>
    public string Display => Kind == WriteActionKind.Grid
        ? Description
        : TextFormat.Plural(System.Globalization.CultureInfo.GetCultureInfo("de-DE"), Rows, DataText.WriteActionRowsOne, DataText.WriteActionRowsOther, Description);
}

/// <summary>A flush stopped at <see cref="Operation"/>; everything of this flush has been rolled back to its savepoint.</summary>
public abstract class FlushException(PendingOperation operation, string message, Exception? inner = null) : Exception(message, inner)
{
    public PendingOperation Operation { get; } = operation;
}

/// <summary>Another session holds a lock on the row (ORA-30006 after the wait, ORA-00054).</summary>
public sealed class LockConflictException(PendingOperation operation, DatabaseException error)
    : FlushException(operation, DataText.RowLockedByOtherSession, error)
{
    public DatabaseException Error { get; } = error;
}

/// <param name="Column">Index into <c>TableDetails.Columns</c>.</param>
/// <param name="Expected">Value the change was based on.</param>
/// <param name="Actual">Value in the database now.</param>
public sealed record ConcurrencyDifference(int Column, object? Expected, object? Actual);

/// <summary>Someone changed a column the user is changing since it was loaded (lost update prevented).</summary>
public sealed class ConcurrencyConflictException(PendingOperation operation, IReadOnlyList<ConcurrencyDifference> differences)
    : FlushException(operation, DataText.RowChangedByOthers)
{
    public IReadOnlyList<ConcurrencyDifference> Differences { get; } = differences;
}

/// <summary>The row to update or delete no longer exists.</summary>
public sealed class RowGoneException(PendingOperation operation)
    : FlushException(operation, DataText.RowGone);

/// <summary>Oracle refused the statement (constraint, NOT NULL, value too large, privileges …).</summary>
public sealed class WriteFailedException(PendingOperation operation, DatabaseException error)
    : FlushException(operation, Describe(error), error)
{
    public DatabaseException Error { get; } = error;

    /// <summary>What the Oracle error means for the user; the original message follows.</summary>
    public static string Describe(DatabaseException error) => error.ErrorCode switch
    {
        "ORA-00001" => DataText.WriteFailedUnique,
        "ORA-02290" => DataText.WriteFailedCheck,
        "ORA-02291" => DataText.WriteFailedParentMissing,
        "ORA-02292" => DataText.WriteFailedChildrenExist,
        "ORA-01400" or "ORA-01407" => DataText.WriteFailedNotNull,
        "ORA-12899" => DataText.WriteFailedValueTooLong,
        "ORA-01438" => DataText.WriteFailedNumberTooLarge,
        "ORA-01031" => DataText.WriteFailedNoPrivilege,
        "ORA-01456" => DataText.WriteFailedReadOnly,
        _ => DataText.WriteFailedOther,
    } + " " + error.Display;
}

/// <summary>
/// Writing in a workspace's transaction (v2, WP-09): flushes go into the session's transaction (opened on the
/// first flush), each one behind its own savepoint so it can be undone; nothing is visible to others before
/// <see cref="CommitAsync"/>. Not available on locked (read-only) sessions.
/// </summary>
public interface IDataEditor
{
    TransactionInfo Transaction { get; }

    /// <summary>
    /// The writes of the open transaction, oldest first – grid flushes and statements alike, because they share the
    /// transaction: rolling back to a savepoint takes back everything after it, whoever wrote it.
    /// <see cref="UndoLastAsync"/> takes back the last one. Empty after commit or rollback.
    /// </summary>
    IReadOnlyList<WriteAction> Actions { get; }

    /// <summary>
    /// The writes taken back in the open transaction that can be applied again (WP-30), the next one first. Any new write,
    /// commit or rollback ends them, and so does a transaction that ended elsewhere (lost connection, locking).
    /// </summary>
    IReadOnlyList<WriteAction> Undone { get; }

    /// <summary>
    /// Locks, checks and writes the operations in order; all or nothing (on failure the flush is rolled back to its
    /// savepoint and a <see cref="FlushException"/> names the operation).
    /// </summary>
    Task<FlushResult> FlushAsync(TableDetails table, IReadOnlyList<PendingOperation> operations, FlushOptions options, CancellationToken cancellationToken);

    /// <summary>Rolls back to the savepoint of the last of <see cref="Actions"/>; the transaction stays open.</summary>
    /// <param name="expected">
    /// The action the user chose to take back (the ↶ tooltip names it); if another one has become the last meanwhile,
    /// a <see cref="Connections.RefusedException"/> instead. Null: whichever is last.
    /// </param>
    /// <returns>The action taken back; null if there is none.</returns>
    Task<WriteAction?> UndoLastAsync(Guid? expected, CancellationToken cancellationToken);

    /// <summary>
    /// Rolls back to the savepoint of <paramref name="actionId"/> (WP-30): it and every later action are taken back – a
    /// savepoint cannot be rolled back selectively. The transaction stays open.
    /// </summary>
    /// <param name="newest">
    /// The newest action the user saw; if another one was written meanwhile, a <see cref="Connections.RefusedException"/>
    /// instead (it would be taken back unseen).
    /// </param>
    /// <returns>The actions taken back, newest first.</returns>
    Task<IReadOnlyList<WriteAction>> UndoToAsync(Guid actionId, Guid newest, CancellationToken cancellationToken);

    /// <summary>
    /// Runs the statement of a write taken back again (WP-30), with the same bind values. It must be the next of
    /// <see cref="Undone"/> and a <see cref="WriteActionKind.Statement"/>; grid writes are applied again by a flush with
    /// <see cref="FlushOptions.RedoOf"/>. The data may have changed meanwhile: the caller compares the rows.
    /// </summary>
    Task<WriteAction> RedoAsync(Guid actionId, CancellationToken cancellationToken);

    Task CommitAsync(CancellationToken cancellationToken);

    Task RollbackAsync(CancellationToken cancellationToken);

    /// <summary>
    /// One INSERT, UPDATE, DELETE or MERGE – from the LINQ console (ADR 0011) or the SQL editor (ADR 0014) – in the
    /// workspace's transaction, begun if there is none; on failure rolled back to its own savepoint. Becomes one of
    /// <see cref="Actions"/>. Like any UPDATE it waits for rows another session has locked – cancel through the token.
    /// </summary>
    Task<WriteAction> ExecuteAsync(QuerySpec statement, CancellationToken cancellationToken);

    /// <summary>
    /// One DDL statement from the SQL editor (WP-22, ADR 0019) – only while no transaction is open (the caller rolls back
    /// after asking the user), never in a locked workspace. Oracle commits it at once: no action, nothing to undo.
    /// </summary>
    Task ExecuteDdlAsync(string statement, CancellationToken cancellationToken);
}

/// <summary>A session holding locks on a table (<c>V$LOCKED_OBJECT</c>/<c>V$SESSION</c>), for the lock conflict dialog.</summary>
public sealed record LockHolder(int Sid, string? User, string? OsUser, string? Machine, string? Program, string? Module, string? Action, DateTime? LogonTime);
