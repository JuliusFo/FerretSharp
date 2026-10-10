using System.Data;
using System.Data.Common;
using System.Text.RegularExpressions;
using FerretSharp.Core.Connections;
using FerretSharp.Core.Query;
using FerretSharp.Core.Resources;
using Oracle.ManagedDataAccess.Client;

namespace FerretSharp.Core.Oracle;

/// <summary>Transactions of the session (ADR 0006): read-only snapshots of locked sessions, writing transactions, the write path.</summary>
public sealed partial class OracleSession
{
    /// <summary>Label of transaction control in errors (there is no statement text).</summary>
    private const string TransactionControl = "(Transaktionssteuerung)";

    private static readonly Regex SavepointName = new(@"\A[A-Z][A-Z0-9_]{0,29}\z", RegexOptions.CultureInvariant);

    /// <summary>The ODP.NET transaction and what it is – one field, so the two cannot disagree.</summary>
    private OpenTransaction? _open;

    /// <summary>Locked: always in a read-only transaction (also when restarting one failed), writing transactions are refused.</summary>
    private bool _readOnlySnapshots;

    private sealed record OpenTransaction(OracleTransaction Handle, TransactionInfo Info);

    public TransactionInfo Transaction => _open?.Info ?? TransactionInfo.None;

    /// <summary>Locked: always in a read-only transaction, writing transactions are refused.</summary>
    public bool UsesReadOnlySnapshots => _readOnlySnapshots;

    /// <summary>
    /// Locks the session: from now on it always runs in a read-only transaction, so Oracle rejects any DML
    /// (ORA-01456). Used for profiles marked read-only (Prod by default).
    /// </summary>
    public Task UseReadOnlySnapshotsAsync(CancellationToken cancellationToken) =>
        ExclusiveAsync(async () =>
        {
            if (Transaction.Mode == TransactionMode.ReadWrite)
            {
                throw new RefusedException(OracleText.WriteTransactionOpen);
            }

            _readOnlySnapshots = true;
            await RestartSnapshotCoreAsync(cancellationToken);
        }, cancellationToken);

    /// <summary>
    /// Unlocks the session (WP-10, the user's explicit decision): ends the read-only transaction; afterwards it may open
    /// a writing transaction like a session of an editable profile. Does nothing if the session is not locked.
    /// </summary>
    public Task StopReadOnlySnapshotsAsync(CancellationToken cancellationToken) =>
        ExclusiveAsync(async () =>
        {
            if (!_readOnlySnapshots)
            {
                return;
            }

            await EndTransactionCoreAsync(rollback: true);
            _readOnlySnapshots = false;
        }, cancellationToken);

    /// <summary>Starts a new snapshot (new read-only transaction) in a locked session; does nothing otherwise.</summary>
    public Task RefreshSnapshotAsync(CancellationToken cancellationToken) =>
        _readOnlySnapshots ? ExclusiveAsync(() => RestartSnapshotCoreAsync(cancellationToken), cancellationToken) : Task.CompletedTask;

    /// <summary>Opens a transaction that may write (WP-09). Refused in a locked session.</summary>
    public Task BeginTransactionAsync(CancellationToken cancellationToken) =>
        ExclusiveAsync(() =>
        {
            if (_readOnlySnapshots)
            {
                throw new RefusedException(OracleText.ConnectionReadOnly);
            }

            if (_open is not null)
            {
                throw new RefusedException(OracleText.TransactionAlreadyOpen);
            }

            _open = new OpenTransaction(BeginCoreTransaction(TransactionControl), new TransactionInfo(TransactionMode.ReadWrite, DateTimeOffset.Now));
            return Task.CompletedTask;
        }, cancellationToken);

    /// <summary>Marks a point in the writing transaction that <see cref="RollbackToSavepointAsync"/> returns to.</summary>
    public Task SavepointAsync(string name, CancellationToken cancellationToken) =>
        InWritingTransactionAsync(name, "SAVEPOINT " + name, cancellationToken);

    /// <summary>Undoes everything since the savepoint; the transaction stays open.</summary>
    public Task RollbackToSavepointAsync(string name, CancellationToken cancellationToken) =>
        InWritingTransactionAsync(name, "ROLLBACK TO SAVEPOINT " + name, cancellationToken);

    /// <summary>Commits the writing transaction.</summary>
    public Task CommitAsync(CancellationToken cancellationToken) =>
        ExclusiveAsync(async () =>
        {
            var transaction = WritingTransaction();
            try
            {
                await TransactionControlAsync(() => transaction.CommitAsync(cancellationToken));
            }
            catch (OracleStatementException ex) when (
                ex.Oracle is null or { Number: OracleErrorCodes.TransactionRolledBack } || _connection.State != ConnectionState.Open)
            {
                // Oracle rolled the transaction back (deferred constraint violated) or the session is gone: nothing is open any more.
                await EndTransactionQuietlyAsync();
                throw;
            }

            await EndTransactionCoreAsync(rollback: false);
        }, cancellationToken);

    /// <summary>
    /// Rolls the transaction back. A locked session then continues in a new snapshot; otherwise there is no
    /// transaction afterwards.
    /// </summary>
    public Task RollbackAsync(CancellationToken cancellationToken) =>
        ExclusiveAsync(() => _readOnlySnapshots ? RestartSnapshotCoreAsync(cancellationToken) : EndTransactionCoreAsync(rollback: true), cancellationToken);

    /// <summary>
    /// The only way to write: a single INSERT/UPDATE/DELETE/MERGE (<see cref="IsWriteStatement"/>) inside an open
    /// transaction – never autocommit, never DDL. In a locked session Oracle rejects it (ORA-01456).
    /// </summary>
    /// <returns>Affected rows and the values of output parameters (<c>RETURNING … INTO</c>).</returns>
    internal async Task<NonQueryResult> ExecuteNonQueryAsync(string sql, IReadOnlyList<QueryParameter> parameters, CancellationToken cancellationToken)
    {
        if (!IsWriteStatement(sql))
        {
            throw new InvalidOperationException("Writes only take a single INSERT, UPDATE, DELETE or MERGE statement.");
        }

        return await ExclusiveAsync(() =>
        {
            if (_open is null)
            {
                throw new RefusedException(OracleText.WriteOnlyInTransaction);
            }

            return RunAsync(sql, parameters, async (command, ct) =>
                new NonQueryResult(await command.ExecuteNonQueryAsync(ct), OracleParameters.Outputs(command)), cancellationToken);
        }, cancellationToken);
    }

    /// <summary>
    /// The schema path (WP-22, ADR 0019), next to the write path and just as narrow: a single DDL statement
    /// (<see cref="IsDdlStatement"/>), never in a locked session and never while a transaction is open – Oracle commits
    /// before and after DDL, so it would commit the open transaction silently. A locked session always has its read-only
    /// transaction open, so it is refused twice.
    /// </summary>
    internal async Task ExecuteDdlAsync(string sql, CancellationToken cancellationToken)
    {
        if (!IsDdlStatement(sql))
        {
            throw new InvalidOperationException("The schema path only takes a single DDL statement – no TRUNCATE, PL/SQL or bind variables.");
        }

        await ExclusiveAsync(() =>
        {
            if (_readOnlySnapshots)
            {
                throw new RefusedException(OracleText.ConnectionReadOnly);
            }

            if (_open is not null)
            {
                throw new RefusedException(OracleText.DdlOnlyWithoutTransaction);
            }

            return RunDdlCoreAsync(sql, cancellationToken);
        }, cancellationToken);
    }

    /// <summary>
    /// ALTER TABLE first takes the table with <c>LOCK TABLE … IN EXCLUSIVE MODE NOWAIT</c> and holds it until Oracle commits
    /// before the DDL: with uncommitted DML of another session on the table, ALTER TABLE … ADD would wait for it and could
    /// not be cancelled (ADR 0019). Other DDL either fails at once itself (ORA-00054) or does not conflict (COMMENT, GRANT),
    /// so it runs as it is. Caller holds the gate.
    /// </summary>
    private async Task<int> RunDdlCoreAsync(string sql, CancellationToken cancellationToken)
    {
        await using var hold = SqlScript.Analyze(sql).FirstWord == "ALTER" && SqlScript.DdlTableOf(sql) is { } table
            ? await HoldForDdlCoreAsync(table, cancellationToken)
            : null;
        return await RunAsync(sql, [], (command, ct) => command.ExecuteNonQueryAsync(ct), cancellationToken);
    }

    /// <summary>
    /// Takes the table for the DDL that follows; the DDL's implicit commit releases it. Null if Oracle cannot lock it at all
    /// (no such table, an object of SYS, a remote synonym) – the DDL then reports for itself.
    /// </summary>
    /// <exception cref="TableBusyException">Another session holds locks on the table (ORA-00054).</exception>
    private async Task<OracleTransaction?> HoldForDdlCoreAsync(SqlTableReference table, CancellationToken cancellationToken)
    {
        var name = table.Owner is { } owner ? $"{OracleIdentifier.Quote(owner)}.{OracleIdentifier.Quote(table.Name)}" : OracleIdentifier.Quote(table.Name);
        var sql = $"LOCK TABLE {name} IN EXCLUSIVE MODE NOWAIT";
        var transaction = BeginCoreTransaction(sql);
        try
        {
            await RunAsync(sql, [], (command, ct) => command.ExecuteNonQueryAsync(ct), cancellationToken);
            return transaction;
        }
        catch (OracleStatementException ex) when (ex.Oracle is { } oracle)
        {
            await EndQuietlyAsync(transaction);
            if (oracle.Number == OracleErrorCodes.ResourceBusy)
            {
                throw new TableBusyException(TextFormat.Format(OracleText.DdlTableBusy, table.Owner is null ? table.Name : $"{table.Owner}.{table.Name}"));
            }

            return null;
        }
        catch
        {
            await EndQuietlyAsync(transaction);
            throw;
        }
    }

    private static async Task EndQuietlyAsync(OracleTransaction transaction)
    {
        try
        {
            await transaction.RollbackAsync();
        }
        catch (Exception ex) when (ex is OracleException or InvalidOperationException)
        {
            // connection gone: nothing is held any more
        }

        await transaction.DisposeAsync();
    }

    /// <summary>
    /// Locks rows before writing them: only <c>SELECT … FOR UPDATE WAIT n</c> / <c>NOWAIT</c>
    /// (<see cref="IsLockStatement"/>) and only inside a writing transaction – a lock outside one would be released
    /// at once. Waiting longer than n seconds ends with ORA-30006.
    /// </summary>
    internal async Task<T> ExecuteLockingReaderAsync<T>(
        string sql, IReadOnlyList<QueryParameter> parameters, Func<DbDataReader, CancellationToken, Task<T>> read, CancellationToken cancellationToken)
    {
        if (!IsLockStatement(sql))
        {
            throw new InvalidOperationException("Rows are only locked with SELECT … FOR UPDATE WAIT n.");
        }

        return await ExclusiveAsync(() =>
        {
            if (Transaction.Mode != TransactionMode.ReadWrite)
            {
                throw new RefusedException(OracleText.LockOnlyInWriteTransaction);
            }

            return ReadCoreAsync(sql, parameters, read, cancellationToken);
        }, cancellationToken);
    }

    /// <summary>Ends the current transaction (if any) and starts a read-only one. Caller holds the gate.</summary>
    private async Task RestartSnapshotCoreAsync(CancellationToken cancellationToken)
    {
        await EndTransactionCoreAsync(rollback: true);
        const string sql = "SET TRANSACTION READ ONLY";
        var transaction = BeginCoreTransaction(sql);
        try
        {
            await RunAsync(sql, [], (command, ct) => command.ExecuteNonQueryAsync(ct), cancellationToken);
        }
        catch
        {
            await transaction.DisposeAsync();
            throw;
        }

        _open = new OpenTransaction(transaction, new TransactionInfo(TransactionMode.ReadOnly, DateTimeOffset.Now));
    }

    /// <summary>
    /// Starts an ODP.NET transaction. After a fatal error ODP.NET has closed the connection, and BeginTransaction would
    /// throw a bare <see cref="InvalidOperationException"/>; report that as a lost connection like <see cref="CreateCommand"/>.
    /// </summary>
    private OracleTransaction BeginCoreTransaction(string sql)
    {
        if (_connection.State != ConnectionState.Open)
        {
            throw new OracleStatementException(sql, [], null);
        }

        try
        {
            return _connection.BeginTransaction();
        }
        catch (OracleException ex)
        {
            throw new OracleStatementException(sql, [], ex);
        }
    }

    /// <summary>Ends the transaction after a failure; a second error must not hide the first.</summary>
    private async Task EndTransactionQuietlyAsync()
    {
        try
        {
            await EndTransactionCoreAsync(rollback: true);
        }
        catch (Exception ex) when (ex is OracleException or InvalidOperationException)
        {
            // Connection gone: Oracle rolls back on its own.
        }
    }

    /// <summary>Caller holds the gate (or the session is being disposed).</summary>
    private async Task EndTransactionCoreAsync(bool rollback)
    {
        if (_open is not { Handle: var transaction })
        {
            return;
        }

        _open = null;
        try
        {
            if (rollback && _connection.State == ConnectionState.Open)
            {
                await transaction.RollbackAsync();
            }
        }
        finally
        {
            await transaction.DisposeAsync();
        }
    }

    /// <summary>The open writing transaction; refused without one. Caller holds the gate.</summary>
    private OracleTransaction WritingTransaction() =>
        _open is { Info.Mode: TransactionMode.ReadWrite, Handle: var transaction }
            ? transaction
            : throw new RefusedException(OracleText.NoWriteTransaction);

    /// <summary>
    /// Savepoints as statements on the session's transaction, not through <see cref="OracleTransaction.Save"/>/<c>Rollback(name)</c>:
    /// those block the calling (UI) thread and cannot be cancelled – on a connection that went silent the window froze.
    /// As commands they run asynchronously, are cancelled on dispose and report a lost connection like any statement.
    /// The name is checked against <see cref="SavepointName"/>, so the text is safe to build.
    /// </summary>
    private Task InWritingTransactionAsync(string savepoint, string sql, CancellationToken cancellationToken)
    {
        if (!SavepointName.IsMatch(savepoint))
        {
            throw new ArgumentException($"Invalid savepoint name: {savepoint}", nameof(savepoint));
        }

        return ExclusiveAsync(async () =>
        {
            _ = WritingTransaction();
            await RunAsync(sql, [], (command, ct) => command.ExecuteNonQueryAsync(ct), cancellationToken);
        }, cancellationToken);
    }

    /// <summary>
    /// Oracle errors of transaction control carry a label instead of a statement, like those of queries. A connection that
    /// is already closed (ODP.NET closes it after a fatal error and then throws a bare <see cref="InvalidOperationException"/>)
    /// is reported as lost, like <see cref="CreateCommand"/> does.
    /// </summary>
    private async Task TransactionControlAsync(Func<Task> action)
    {
        if (_connection.State != ConnectionState.Open)
        {
            throw new OracleStatementException(TransactionControl, [], null);
        }

        try
        {
            await action();
        }
        catch (OracleException ex)
        {
            throw new OracleStatementException(TransactionControl, [], ex);
        }
        catch (InvalidOperationException) when (_connection.State != ConnectionState.Open)
        {
            throw new OracleStatementException(TransactionControl, [], null);
        }
    }
}
