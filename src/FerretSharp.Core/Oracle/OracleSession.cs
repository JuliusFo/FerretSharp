using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using FerretSharp.Core.Connections;
using FerretSharp.Core.Query;
using Oracle.ManagedDataAccess.Client;

namespace FerretSharp.Core.Oracle;

/// <summary>
/// A statement failed; carries it for the error dialog. <see cref="Exception.InnerException"/> is the
/// <see cref="OracleException"/>, or null if the connection was already closed.
/// </summary>
internal sealed class OracleStatementException(string sql, IReadOnlyList<QueryParameter> parameters, OracleException? inner)
    : Exception(inner?.Message ?? "Die Verbindung zur Datenbank ist getrennt.", inner)
{
    public QuerySpec Statement { get; } = new(sql, parameters);

    public OracleException? Oracle { get; } = inner;
}

/// <param name="Outputs">Output parameters by name (<c>RETURNING ROWID INTO :p_rowid</c>), as text.</param>
internal sealed record NonQueryResult(int Rows, IReadOnlyDictionary<string, object?> Outputs);

/// <summary>Values shown in <c>V$SESSION</c> (MODULE, ACTION, CLIENT_INFO) to identify who holds a session.</summary>
public sealed record SessionContext(string Module, string Action, string? ClientInfo = null);

/// <summary>
/// One long-lived Oracle connection (one per workspace). <see cref="OracleConnection"/> is not thread-safe,
/// so all commands are serialized. <see cref="ExecuteReaderAsync{T}"/> refuses anything but plain queries; writing
/// goes only through the internal <see cref="ExecuteNonQueryAsync"/>, inside an explicit transaction.
/// </summary>
/// <remarks>
/// Transactions (ADR 0006): a locked session (<see cref="UseReadOnlySnapshotsAsync"/>) always runs in a
/// <c>SET TRANSACTION READ ONLY</c> transaction, so Oracle itself rejects DML. That statement must be the first of an
/// ODP.NET transaction – without one, ODP.NET commits after every statement and the read-only transaction ends at
/// once. DDL still runs in a read-only transaction (implicit commit), which is why the statement guards stay.
/// </remarks>
public sealed class OracleSession : IAsyncDisposable
{
    /// <summary>
    /// Errors of a read-only snapshot that a new snapshot fixes: snapshot too old (ORA-01555), table definition
    /// changed since the snapshot (ORA-01466), segment created since the snapshot (ORA-08176, first row of a table
    /// with deferred segment creation).
    /// </summary>
    internal static readonly IReadOnlySet<int> SnapshotErrorNumbers = new HashSet<int> { 1555, 1466, 8176 };

    /// <summary>New snapshots tried per query before the error is reported (ORA-01466 waits 1, 2, 3 s).</summary>
    private const int MaxSnapshotRetries = 3;

    private static readonly Regex SavepointName = new(@"\A[A-Z][A-Z0-9_]{0,29}\z", RegexOptions.CultureInvariant);

    /// <summary>ORA-01013: user requested cancel of current operation.</summary>
    internal const int UserCancelledErrorNumber = 1013;

    /// <summary>ORA-02091: transaction rolled back (a deferred constraint failed at commit).</summary>
    internal const int TransactionRolledBackErrorNumber = 2091;

    /// <summary>A single plain query (SELECT/WITH, no FOR UPDATE) – the only kind the read path runs. See <see cref="StatementGuard"/>.</summary>
    internal static bool IsReadOnlyStatement(string sql) => StatementGuard.IsQuery(sql);

    /// <summary>
    /// A single INSERT, UPDATE, DELETE or MERGE (MERGE since the SQL editor, ADR 0014). Never DDL: it would commit
    /// implicitly, also inside a read-only transaction.
    /// </summary>
    internal static bool IsWriteStatement(string sql) => StatementGuard.IsWrite(sql);

    /// <summary>
    /// Characters of LONG columns fetched with the row (<c>ALL_TAB_COLUMNS.DATA_DEFAULT</c>, <c>ALL_VIEWS.TEXT</c>).
    /// Without it, ODP.NET returns no LONG data. 32767 is the driver's maximum; table data never selects LONG columns.
    /// </summary>
    internal const int LongFetchSize = 32767;

    /// <summary>Label of transaction control in errors (there is no statement text).</summary>
    private const string TransactionControl = "(Transaktionssteuerung)";

    /// <summary>How long disposing waits for a running command after cancelling it, before closing anyway.</summary>
    private static readonly TimeSpan DisposeWait = TimeSpan.FromSeconds(10);

    private readonly OracleConnection _connection;

    /// <summary>Serializes all use of <see cref="_connection"/>; never disposed (callers may still be waiting on it).</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);
    private long _lastRoundTrip = Environment.TickCount64;
    private volatile bool _disposed;
    private volatile OracleCommand? _running;
    private OracleTransaction? _transaction;
    private bool _readOnlySnapshots;

    private OracleSession(OracleConnection connection) => _connection = connection;

    public string ServerVersion => _connection.ServerVersion;

    public TransactionInfo Transaction { get; private set; } = TransactionInfo.None;

    /// <summary>Locked: always in a read-only transaction, writing transactions are refused.</summary>
    public bool UsesReadOnlySnapshots => _readOnlySnapshots;

    public static async Task<OracleSession> OpenAsync(string connectionString, SessionContext context, CancellationToken cancellationToken)
    {
        var connection = new OracleConnection(connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);

            // ODP.NET derives NLS settings from the Windows locale; German Windows gives NLS_SORT=GERMAN, which sorts
            // digits after letters and cannot use B-tree indexes. Browsing needs a predictable, machine-independent order.
            var globalization = connection.GetSessionInfo();
            globalization.Sort = "BINARY";
            globalization.Comparison = "BINARY";
            connection.SetSessionInfo(globalization);

            connection.ModuleName = context.Module;
            connection.ActionName = ToSessionAttribute(context.Action);
            connection.ClientInfo = ToSessionAttribute(context.ClientInfo ?? string.Empty);
            return new OracleSession(connection);
        }
        catch (OracleException ex) when (ex.Number == UserCancelledErrorNumber)
        {
            await connection.DisposeAsync();
            throw new OperationCanceledException("Opening the session was cancelled.", ex, cancellationToken);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    /// <summary>Changes ACTION in <c>V$SESSION</c> (e.g. after renaming a workspace); sent with the next round trip.</summary>
    public async Task SetActionAsync(string action, CancellationToken cancellationToken)
    {
        await EnterAsync(cancellationToken);
        try
        {
            _connection.ActionName = ToSessionAttribute(action);
        }
        finally
        {
            Exit();
        }
    }

    /// <summary>
    /// Makes a value safe for ACTION/CLIENT_INFO: ASCII only, at most 64 characters. ODP.NET (managed, 23.26) breaks
    /// the session when these attributes contain non-ASCII characters – the next round trip fails with ORA-12537 and
    /// the connection is gone (integration test). Workspace names like "Prüfung" are common, so umlauts are
    /// transliterated (ä → ae), accents dropped (é → e) and anything else replaced by '?'.
    /// </summary>
    internal static string ToSessionAttribute(string value)
    {
        const int maxLength = 64;
        var result = new StringBuilder(value.Length);
        foreach (var c in value.Normalize(NormalizationForm.FormC))
        {
            result.Append(c switch
            {
                'ä' => "ae",
                'ö' => "oe",
                'ü' => "ue",
                'Ä' => "Ae",
                'Ö' => "Oe",
                'Ü' => "Ue",
                'ß' => "ss",
                '–' or '—' => "-",
                < '\u0080' when !char.IsControl(c) => c.ToString(),
                _ when char.IsLowSurrogate(c) => "", // the high surrogate already became '?'
                _ when char.IsHighSurrogate(c) => "?",
                _ when c.ToString().Normalize(NormalizationForm.FormD)[0] is var b and < '\u0080' && !char.IsControl(b) => b.ToString(),
                _ => "?",
            });
            if (result.Length >= maxLength)
            {
                break;
            }
        }

        return result.Length > maxLength ? result.ToString(0, maxLength) : result.ToString();
    }

    /// <summary>Round trip with <c>SELECT 1 FROM DUAL</c>.</summary>
    public async Task<TimeSpan> PingAsync(CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        await ExecuteReaderAsync("SELECT 1 FROM DUAL", [], (reader, ct) => reader.ReadAsync(ct), cancellationToken);
        return stopwatch.Elapsed;
    }

    /// <summary>
    /// Keep-alive: pings only if the session has been idle for at least <paramref name="idleFor"/>. A session that is
    /// running a statement or already closed is left alone. False if it did not ping.
    /// </summary>
    public async Task<bool> PingIfIdleAsync(TimeSpan idleFor, CancellationToken cancellationToken)
    {
        var idle = TimeSpan.FromMilliseconds(Environment.TickCount64 - Interlocked.Read(ref _lastRoundTrip));
        if (_disposed || _gate.CurrentCount == 0 || idle < idleFor)
        {
            return false;
        }

        await PingAsync(cancellationToken);
        return true;
    }

    /// <summary>
    /// Runs a query and hands the open reader to <paramref name="read"/>. Cancelling the token cancels the
    /// statement on the server; the session stays usable afterwards.
    /// </summary>
    /// <remarks>
    /// In a locked session, a query that fails because the snapshot cannot serve it (<see cref="SnapshotErrorNumbers"/>)
    /// is retried in a new snapshot (up to <see cref="MaxSnapshotRetries"/> times); <paramref name="read"/> must therefore not keep state across calls.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The statement is not a plain query (see <see cref="IsReadOnlyStatement"/>).</exception>
    public async Task<T> ExecuteReaderAsync<T>(
        string sql,
        IReadOnlyList<QueryParameter> parameters,
        Func<DbDataReader, CancellationToken, Task<T>> read,
        CancellationToken cancellationToken)
    {
        // Tripwire: ExecuteReader would run DML just as well, and without a transaction ODP.NET commits it at once.
        if (!IsReadOnlyStatement(sql))
        {
            throw new InvalidOperationException("Über diesen Weg laufen nur lesende Abfragen (SELECT/WITH ohne FOR UPDATE).");
        }

        await EnterAsync(cancellationToken);
        try
        {
            if (_readOnlySnapshots && _transaction is null)
            {
                // An earlier snapshot restart failed (lost connection, cancelled): a locked session never reads in autocommit.
                await RestartSnapshotCoreAsync(cancellationToken);
            }

            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    return await ReadCoreAsync(sql, parameters, read, cancellationToken);
                }
                catch (OracleStatementException ex) when (
                    _readOnlySnapshots && attempt <= MaxSnapshotRetries && ex.Oracle is { } oracle && SnapshotErrorNumbers.Contains(oracle.Number))
                {
                    // A snapshot taken within about a second of the DDL is still too old for it (ORA-01466).
                    if (oracle.Number == 1466)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(attempt), cancellationToken);
                    }

                    await RestartSnapshotCoreAsync(cancellationToken);
                }
            }
        }
        finally
        {
            Exit();
        }
    }

    /// <summary>
    /// Locks the session: from now on it always runs in a read-only transaction, so Oracle rejects any DML
    /// (ORA-01456). Used for profiles marked read-only (Prod by default).
    /// </summary>
    public async Task UseReadOnlySnapshotsAsync(CancellationToken cancellationToken)
    {
        await EnterAsync(cancellationToken);
        try
        {
            if (_transaction is not null && Transaction.Mode == TransactionMode.ReadWrite)
            {
                throw new InvalidOperationException("Eine schreibende Transaktion ist offen.");
            }

            _readOnlySnapshots = true;
            await RestartSnapshotCoreAsync(cancellationToken);
        }
        finally
        {
            Exit();
        }
    }

    /// <summary>
    /// Unlocks the session (WP-10, the user's explicit decision): ends the read-only transaction; afterwards it may open
    /// a writing transaction like a session of an editable profile. Does nothing if the session is not locked.
    /// </summary>
    public async Task StopReadOnlySnapshotsAsync(CancellationToken cancellationToken)
    {
        await EnterAsync(cancellationToken);
        try
        {
            if (!_readOnlySnapshots)
            {
                return;
            }

            await EndTransactionCoreAsync(rollback: true);
            _readOnlySnapshots = false;
        }
        finally
        {
            Exit();
        }
    }

    /// <summary>Starts a new snapshot (new read-only transaction) in a locked session; does nothing otherwise.</summary>
    public async Task RefreshSnapshotAsync(CancellationToken cancellationToken)
    {
        if (!_readOnlySnapshots)
        {
            return;
        }

        await EnterAsync(cancellationToken);
        try
        {
            await RestartSnapshotCoreAsync(cancellationToken);
        }
        finally
        {
            Exit();
        }
    }

    /// <summary>Opens a transaction that may write (WP-09). Refused in a locked session.</summary>
    public async Task BeginTransactionAsync(CancellationToken cancellationToken)
    {
        await EnterAsync(cancellationToken);
        try
        {
            if (_readOnlySnapshots)
            {
                throw new InvalidOperationException("Die Verbindung ist schreibgeschützt.");
            }

            if (_transaction is not null)
            {
                throw new InvalidOperationException("Es ist bereits eine Transaktion offen.");
            }

            _transaction = BeginCoreTransaction(TransactionControl);
            Transaction = new TransactionInfo(TransactionMode.ReadWrite, DateTimeOffset.Now);
        }
        finally
        {
            Exit();
        }
    }

    /// <summary>Marks a point in the writing transaction that <see cref="RollbackToSavepointAsync"/> returns to.</summary>
    public Task SavepointAsync(string name, CancellationToken cancellationToken) =>
        InWritingTransactionAsync(name, transaction => transaction.Save(name), cancellationToken);

    /// <summary>Undoes everything since the savepoint; the transaction stays open.</summary>
    public Task RollbackToSavepointAsync(string name, CancellationToken cancellationToken) =>
        InWritingTransactionAsync(name, transaction => transaction.Rollback(name), cancellationToken);

    /// <summary>Commits the writing transaction.</summary>
    public async Task CommitAsync(CancellationToken cancellationToken)
    {
        await EnterAsync(cancellationToken);
        try
        {
            if (_transaction is null || Transaction.Mode != TransactionMode.ReadWrite)
            {
                throw new InvalidOperationException("Es ist keine schreibende Transaktion offen.");
            }

            try
            {
                await GuardAsync(() => _transaction.CommitAsync(cancellationToken));
            }
            catch (OracleStatementException ex) when (ex.Oracle is null or { Number: TransactionRolledBackErrorNumber } || _connection.State != ConnectionState.Open)
            {
                // Oracle rolled the transaction back (deferred constraint violated) or the session is gone: nothing is open any more.
                await EndTransactionQuietlyAsync();
                throw;
            }

            await EndTransactionCoreAsync(rollback: false);
        }
        finally
        {
            Exit();
        }
    }

    /// <summary>
    /// Rolls the transaction back. A locked session then continues in a new snapshot; otherwise there is no
    /// transaction afterwards.
    /// </summary>
    public async Task RollbackAsync(CancellationToken cancellationToken)
    {
        await EnterAsync(cancellationToken);
        try
        {
            if (_readOnlySnapshots)
            {
                await RestartSnapshotCoreAsync(cancellationToken);
            }
            else
            {
                await EndTransactionCoreAsync(rollback: true);
            }
        }
        finally
        {
            Exit();
        }
    }

    /// <summary>
    /// The only way to write: a single INSERT/UPDATE/DELETE/MERGE (<see cref="IsWriteStatement"/>) inside an open
    /// transaction – never autocommit, never DDL. In a locked session Oracle rejects it (ORA-01456).
    /// </summary>
    /// <returns>Affected rows and the values of output parameters (<c>RETURNING … INTO</c>).</returns>
    internal async Task<NonQueryResult> ExecuteNonQueryAsync(string sql, IReadOnlyList<QueryParameter> parameters, CancellationToken cancellationToken)
    {
        if (!IsWriteStatement(sql))
        {
            throw new InvalidOperationException("Geschrieben wird nur mit einzelnen INSERT-, UPDATE-, DELETE- oder MERGE-Statements.");
        }

        await EnterAsync(cancellationToken);
        try
        {
            if (_transaction is null)
            {
                throw new InvalidOperationException("Geschrieben wird nur innerhalb einer Transaktion.");
            }

            await using var command = CreateCommand(sql, parameters);
            await using var cancelRegistration = cancellationToken.Register(static c => ((OracleCommand)c!).Cancel(), command);
            try
            {
                var rows = await command.ExecuteNonQueryAsync(cancellationToken);
                var outputs = command.Parameters.Cast<OracleParameter>()
                    .Where(p => p.Direction == ParameterDirection.Output)
                    .ToDictionary(p => p.ParameterName, p => p.Value is DBNull or null ? null : (object?)p.Value.ToString());
                return new NonQueryResult(rows, outputs);
            }
            catch (OracleException ex) when (ex.Number == UserCancelledErrorNumber)
            {
                throw new OperationCanceledException("The statement was cancelled.", ex, cancellationToken);
            }
            catch (OracleException ex)
            {
                throw new OracleStatementException(sql, parameters, ex);
            }
        }
        finally
        {
            Exit();
        }
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
            throw new InvalidOperationException("Gesperrt wird nur mit SELECT … FOR UPDATE WAIT n.");
        }

        await EnterAsync(cancellationToken);
        try
        {
            if (_transaction is null || Transaction.Mode != TransactionMode.ReadWrite)
            {
                throw new InvalidOperationException("Zeilen werden nur innerhalb einer schreibenden Transaktion gesperrt.");
            }

            return await ReadCoreAsync(sql, parameters, read, cancellationToken);
        }
        finally
        {
            Exit();
        }
    }

    /// <summary>A single SELECT ending in FOR UPDATE WAIT n or FOR UPDATE NOWAIT – never one that waits forever.</summary>
    internal static bool IsLockStatement(string sql) => StatementGuard.IsLock(sql);

    /// <summary>
    /// Rolls back an open transaction first: nothing uncommitted may survive by accident. A running command (grid page,
    /// keep-alive ping) is cancelled and finishes before the connection closes – <see cref="OracleConnection"/> is not
    /// thread-safe. Callers still waiting for the session get an <see cref="OperationCanceledException"/>.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CancelRunningCommand();
        var entered = await _gate.WaitAsync(DisposeWait);
        try
        {
            // If the command did not stop in time, closing the connection ends the session, and Oracle rolls back.
            if (entered)
            {
                await EndTransactionQuietlyAsync();
            }
        }
        finally
        {
            await _connection.DisposeAsync();
            if (entered)
            {
                _gate.Release();
            }
        }
    }

    private void CancelRunningCommand()
    {
        try
        {
            _running?.Cancel();
        }
        catch (Exception ex) when (ex is OracleException or InvalidOperationException or ObjectDisposedException)
        {
            // the command already finished
        }
    }

    /// <summary>
    /// Takes the gate. Once the session is disposed (workspace closed, disconnected), the call is abandoned like a
    /// cancelled one – also for callers that were already waiting; nobody is interested in its result any more.
    /// </summary>
    private async Task EnterAsync(CancellationToken cancellationToken)
    {
        if (!_disposed)
        {
            await _gate.WaitAsync(cancellationToken);
            if (!_disposed)
            {
                return;
            }

            _gate.Release();
        }

        throw new OperationCanceledException("The session was closed.");
    }

    private void Exit()
    {
        _running = null;
        Interlocked.Exchange(ref _lastRoundTrip, Environment.TickCount64);
        _gate.Release();
    }

    /// <summary>
    /// The optimizer's plan of a plain query without running it (ADR 0012): <c>EXPLAIN PLAN</c> writes the plan into
    /// <c>PLAN_TABLE</c> – a global temporary table, private to this session – under an id made here; <paramref name="read"/>
    /// gets those rows (<c>ID</c>, <c>PARENT_ID</c>, <c>DEPTH</c>, <c>OPERATION</c> …, ordered by id), then they are deleted.
    /// The only statements besides queries that leave this session's read path, and only these, built here. The bind
    /// placeholders stay unbound: Oracle plans them as text. Not in a read-only transaction: Oracle refuses there.
    /// </summary>
    /// <exception cref="InvalidOperationException">Not a plain query, or the session is locked (read-only transaction).</exception>
    internal async Task<T> ExplainPlanAsync<T>(string sql, Func<DbDataReader, CancellationToken, Task<T>> read, CancellationToken cancellationToken)
    {
        if (!IsReadOnlyStatement(sql))
        {
            throw new InvalidOperationException("Nur Abfragen (SELECT/WITH) lassen sich erklären.");
        }

        await EnterAsync(cancellationToken);
        try
        {
            if (_readOnlySnapshots || Transaction.Mode == TransactionMode.ReadOnly)
            {
                throw new InvalidOperationException("In einer READ ONLY-Transaktion lehnt Oracle EXPLAIN PLAN ab.");
            }

            var id = "FS" + Guid.NewGuid().ToString("N")[..24]; // STATEMENT_ID is a literal, at most 30 characters
            var explain = $"EXPLAIN PLAN SET STATEMENT_ID = '{id}' FOR {sql.TrimEnd().TrimEnd(';')}";
            try
            {
                await using (var command = CreateCommand(explain, []))
                {
                    await using var cancelRegistration = cancellationToken.Register(static c => ((OracleCommand)c!).Cancel(), command);
                    await command.ExecuteNonQueryAsync(cancellationToken);
                }

                return await ReadCoreAsync(
                    """
                    SELECT ID, PARENT_ID, DEPTH, OPERATION, OPTIONS, OBJECT_OWNER, OBJECT_NAME, COST, CARDINALITY, BYTES,
                           ACCESS_PREDICATES, FILTER_PREDICATES
                      FROM PLAN_TABLE
                     WHERE STATEMENT_ID = :id
                     ORDER BY ID
                    """,
                    [new QueryParameter("id", id)], read, cancellationToken);
            }
            catch (OracleException ex) when (ex.Number == UserCancelledErrorNumber)
            {
                throw new OperationCanceledException("The statement was cancelled.", ex, cancellationToken);
            }
            catch (OracleException ex)
            {
                throw new OracleStatementException(explain, [], ex);
            }
            finally
            {
                try
                {
                    await using var delete = CreateCommand("DELETE FROM PLAN_TABLE WHERE STATEMENT_ID = :id", [new QueryParameter("id", id)]);
                    await delete.ExecuteNonQueryAsync(CancellationToken.None);
                }
                catch (Exception ex) when (ex is OracleException or OracleStatementException)
                {
                    // the rows are private to the session and vanish with it
                }
            }
        }
        finally
        {
            Exit();
        }
    }

    private async Task<T> ReadCoreAsync<T>(
        string sql, IReadOnlyList<QueryParameter> parameters, Func<DbDataReader, CancellationToken, Task<T>> read, CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(sql, parameters);
        await using var cancelRegistration = cancellationToken.Register(static c => ((OracleCommand)c!).Cancel(), command);
        try
        {
            await using var reader = (OracleDataReader)await command.ExecuteReaderAsync(cancellationToken);
            reader.SuppressGetDecimalInvalidCastException = true; // NUMBER(38) exceeds decimal
            return await read(reader, cancellationToken);
        }
        catch (OracleException ex) when (ex.Number == UserCancelledErrorNumber)
        {
            throw new OperationCanceledException("The query was cancelled.", ex, cancellationToken);
        }
        catch (OracleException ex)
        {
            throw new OracleStatementException(sql, parameters, ex);
        }
    }

    /// <summary>Commands join the connection's open transaction by themselves (integration test).</summary>
    private OracleCommand CreateCommand(string sql, IReadOnlyList<QueryParameter> parameters)
    {
        // After a fatal error (ORA-03113 …) ODP.NET closes the connection; report that like the error itself.
        if (_connection.State != ConnectionState.Open)
        {
            throw new OracleStatementException(sql, parameters, null);
        }

        var command = _connection.CreateCommand();
        command.BindByName = true;
        command.InitialLONGFetchSize = LongFetchSize;
        // Whole LOBs come with the row (LOB editor, locked rows of the concurrency check). Grid queries never select a
        // LOB column itself, only a preview and its length (QueryBuilder).
        command.InitialLOBFetchSize = -1;
        command.CommandText = sql;
        foreach (var parameter in parameters)
        {
            command.Parameters.Add(ToOracleParameter(parameter));
        }

        _running = command; // cancelled by DisposeAsync
        return command;
    }

    /// <summary>Ends the current transaction (if any) and starts a read-only one. Caller holds the gate.</summary>
    private async Task RestartSnapshotCoreAsync(CancellationToken cancellationToken)
    {
        await EndTransactionCoreAsync(rollback: true);
        const string sql = "SET TRANSACTION READ ONLY";
        var transaction = BeginCoreTransaction(sql);
        try
        {
            await using var command = CreateCommand(sql, []);
            await GuardAsync(() => command.ExecuteNonQueryAsync(cancellationToken), sql);
        }
        catch
        {
            await transaction.DisposeAsync();
            throw;
        }

        _transaction = transaction;
        Transaction = new TransactionInfo(TransactionMode.ReadOnly, DateTimeOffset.Now);
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
        if (_transaction is not { } transaction)
        {
            return;
        }

        _transaction = null;
        Transaction = TransactionInfo.None;
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

    private async Task InWritingTransactionAsync(string savepoint, Action<OracleTransaction> action, CancellationToken cancellationToken)
    {
        if (!SavepointName.IsMatch(savepoint))
        {
            throw new ArgumentException($"Ungültiger Savepoint-Name: {savepoint}", nameof(savepoint));
        }

        await EnterAsync(cancellationToken);
        try
        {
            if (_transaction is null || Transaction.Mode != TransactionMode.ReadWrite)
            {
                throw new InvalidOperationException("Es ist keine schreibende Transaktion offen.");
            }

            var transaction = _transaction;
            await GuardAsync(() =>
            {
                action(transaction);
                return Task.CompletedTask;
            });
        }
        finally
        {
            Exit();
        }
    }

    /// <summary>Oracle errors of transaction control become <see cref="OracleStatementException"/> like those of queries.</summary>
    private static async Task GuardAsync(Func<Task> action, string sql = TransactionControl)
    {
        try
        {
            await action();
        }
        catch (OracleException ex)
        {
            throw new OracleStatementException(sql, [], ex);
        }
    }

    private static OracleParameter ToOracleParameter(QueryParameter parameter)
    {
        if (parameter.Output)
        {
            return new OracleParameter(parameter.Name, OracleDbType.Varchar2, 4000) { Direction = ParameterDirection.Output };
        }

        var result = new OracleParameter(parameter.Name, parameter.Value ?? DBNull.Value);
        switch (parameter.Type)
        {
            case OracleTypeHint.NVarchar2:
                result.OracleDbType = OracleDbType.NVarchar2;
                break;
            case OracleTypeHint.Varchar2:
                result.OracleDbType = OracleDbType.Varchar2;
                break;
            case OracleTypeHint.Char:
                result.OracleDbType = OracleDbType.Char;
                break;
            case OracleTypeHint.Number:
                result.OracleDbType = OracleDbType.Decimal;
                break;
            case OracleTypeHint.Date:
                result.OracleDbType = OracleDbType.Date;
                break;
            case OracleTypeHint.TimeStamp:
                result.OracleDbType = OracleDbType.TimeStamp;
                break;
            case OracleTypeHint.Raw:
                result.OracleDbType = OracleDbType.Raw;
                break;
            case OracleTypeHint.Clob:
                result.OracleDbType = OracleDbType.Clob;
                break;
            case OracleTypeHint.NClob:
                result.OracleDbType = OracleDbType.NClob;
                break;
            case OracleTypeHint.Blob:
                result.OracleDbType = OracleDbType.Blob;
                break;
        }

        return result;
    }
}
