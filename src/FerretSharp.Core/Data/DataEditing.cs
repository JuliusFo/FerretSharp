using FerretSharp.Core.Connections;
using FerretSharp.Core.Query;
using FerretSharp.Core.Schema;

namespace FerretSharp.Core.Data;

/// <param name="LockWaitSeconds">How long to wait for a row another session has locked (then <see cref="LockConflictException"/>).</param>
/// <param name="Overwrite">Changes (<see cref="RowChange.Id"/>) whose concurrency conflict the user chose to overwrite.</param>
public sealed record FlushOptions(int LockWaitSeconds = DmlBuilder.DefaultLockWaitSeconds, IReadOnlySet<Guid>? Overwrite = null);

/// <param name="NewKeys">Key of each inserted row by <see cref="RowChange.Id"/>.</param>
/// <param name="InsertedRows">Inserted rows as Oracle stored them (defaults, identity values, triggers).</param>
public sealed record FlushResult(IReadOnlyDictionary<Guid, RowKey> NewKeys, IReadOnlyDictionary<Guid, RowData> InsertedRows)
{
    public static readonly FlushResult Empty = new(new Dictionary<Guid, RowKey>(), new Dictionary<Guid, RowData>());
}

/// <summary>A flush stopped at <see cref="Operation"/>; everything of this flush has been rolled back to its savepoint.</summary>
public abstract class FlushException(PendingOperation operation, string message, Exception? inner = null) : Exception(message, inner)
{
    public PendingOperation Operation { get; } = operation;
}

/// <summary>Another session holds a lock on the row (ORA-30006 after the wait, ORA-00054).</summary>
public sealed class LockConflictException(PendingOperation operation, DatabaseException error)
    : FlushException(operation, "Die Zeile ist von einer anderen Session gesperrt.", error)
{
    public DatabaseException Error { get; } = error;
}

/// <param name="Column">Index into <c>TableDetails.Columns</c>.</param>
/// <param name="Expected">Value the change was based on.</param>
/// <param name="Actual">Value in the database now.</param>
public sealed record ConcurrencyDifference(int Column, object? Expected, object? Actual);

/// <summary>Someone changed a column the user is changing since it was loaded (lost update prevented).</summary>
public sealed class ConcurrencyConflictException(PendingOperation operation, IReadOnlyList<ConcurrencyDifference> differences)
    : FlushException(operation, "Die Zeile wurde inzwischen von jemand anderem geändert.")
{
    public IReadOnlyList<ConcurrencyDifference> Differences { get; } = differences;
}

/// <summary>The row to update or delete no longer exists.</summary>
public sealed class RowGoneException(PendingOperation operation)
    : FlushException(operation, "Die Zeile gibt es nicht mehr – sie wurde inzwischen gelöscht.");

/// <summary>Oracle refused the statement (constraint, NOT NULL, value too large, privileges …).</summary>
public sealed class WriteFailedException(PendingOperation operation, DatabaseException error)
    : FlushException(operation, Describe(error), error)
{
    public DatabaseException Error { get; } = error;

    /// <summary>What the Oracle error means for the user; the original message follows.</summary>
    public static string Describe(DatabaseException error) => error.ErrorCode switch
    {
        "ORA-00001" => "Eindeutigkeit verletzt – diesen Wert gibt es schon.",
        "ORA-02290" => "Check-Constraint verletzt.",
        "ORA-02291" => "Fremdschlüssel verletzt – den referenzierten Datensatz gibt es nicht.",
        "ORA-02292" => "Fremdschlüssel verletzt – es gibt noch abhängige Datensätze.",
        "ORA-01400" or "ORA-01407" => "Pflichtfeld fehlt (NOT NULL).",
        "ORA-12899" => "Wert zu lang für die Spalte.",
        "ORA-01438" => "Zahl zu groß für die Spalte.",
        "ORA-01031" => "Keine Rechte, diese Tabelle zu ändern.",
        "ORA-01456" => "Die Verbindung ist schreibgeschützt.",
        _ => "Oracle hat die Änderung abgelehnt.",
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

    /// <summary>Flushes that <see cref="UndoLastFlushAsync"/> can still take back.</summary>
    int FlushCount { get; }

    /// <summary>
    /// Locks, checks and writes the operations in order; all or nothing (on failure the flush is rolled back to its
    /// savepoint and a <see cref="FlushException"/> names the operation).
    /// </summary>
    Task<FlushResult> FlushAsync(TableDetails table, IReadOnlyList<PendingOperation> operations, FlushOptions options, CancellationToken cancellationToken);

    /// <summary>Rolls back to the savepoint of the last flush; the transaction stays open.</summary>
    Task UndoLastFlushAsync(CancellationToken cancellationToken);

    Task CommitAsync(CancellationToken cancellationToken);

    Task RollbackAsync(CancellationToken cancellationToken);

    /// <summary>
    /// One INSERT, UPDATE, DELETE or MERGE – from the LINQ console (ADR 0011) or the SQL editor (ADR 0014) – in the
    /// workspace's transaction, begun if there is none; on failure rolled back to its own savepoint. Not part of
    /// <see cref="FlushCount"/>: undoing a grid write stays with the grid. Like any UPDATE it waits for rows another
    /// session has locked – cancel through the token.
    /// </summary>
    /// <returns>Affected rows.</returns>
    Task<int> ExecuteAsync(QuerySpec statement, CancellationToken cancellationToken);
}

/// <summary>A session holding locks on a table (<c>V$LOCKED_OBJECT</c>/<c>V$SESSION</c>), for the lock conflict dialog.</summary>
public sealed record LockHolder(int Sid, string? User, string? OsUser, string? Machine, string? Program, string? Module, string? Action, DateTime? LogonTime);
