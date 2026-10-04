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

    private static readonly Regex WriteStart = new(@"\A(?:INSERT|UPDATE|DELETE)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex SavepointName = new(@"\A[A-Z][A-Z0-9_]{0,29}\z", RegexOptions.CultureInvariant);
    /// <summary>ORA-01013: user requested cancel of current operation.</summary>
    internal const int UserCancelledErrorNumber = 1013;

    private static readonly Regex LeadingComments = new(@"\A(?:\s+|--[^\n]*(?:\n|\z)|/\*.*?\*/)*", RegexOptions.Singleline | RegexOptions.CultureInvariant);
    private static readonly Regex QueryStart = new(@"\A(?:SELECT|WITH)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex StringLiterals = new("'(?:[^']|'')*'", RegexOptions.CultureInvariant);
    private static readonly Regex ForUpdate = new(@"\bFOR\s+UPDATE\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// A plain query: starts with SELECT or WITH (after whitespace and comments), no several statements and no
    /// FOR UPDATE (row locks). Not a SQL parser – a guard against programming mistakes, since all SQL comes from
    /// <c>QueryBuilder</c> and <c>OracleSchemaReader</c>. Values are always bind variables, never part of the text.
    /// </summary>
    internal static bool IsReadOnlyStatement(string sql)
    {
        var body = sql[LeadingComments.Match(sql).Length..];
        if (!QueryStart.IsMatch(body))
        {
            return false;
        }

        var withoutLiterals = StringLiterals.Replace(body, "''");
        return !ForUpdate.IsMatch(withoutLiterals) && !withoutLiterals.TrimEnd().TrimEnd(';').Contains(';', StringComparison.Ordinal);
    }

    /// <summary>
    /// A single INSERT, UPDATE or DELETE (after whitespace and comments). Never DDL: it would commit implicitly, also
    /// inside a read-only transaction.
    /// </summary>
    internal static bool IsWriteStatement(string sql)
    {
        var body = sql[LeadingComments.Match(sql).Length..];
        var withoutLiterals = StringLiterals.Replace(body, "''");
        return WriteStart.IsMatch(body) && !withoutLiterals.TrimEnd().TrimEnd(';').Contains(';', StringComparison.Ordinal);
    }

    /// <summary>
    /// Characters of LONG columns fetched with the row (<c>ALL_TAB_COLUMNS.DATA_DEFAULT</c>, <c>ALL_VIEWS.TEXT</c>).
    /// Without it, ODP.NET returns no LONG data. 32767 is the driver's maximum; table data never selects LONG columns.
    /// </summary>
    internal const int LongFetchSize = 32767;

    private readonly OracleConnection _connection;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private long _lastRoundTrip = Environment.TickCount64;
    private volatile bool _disposed;
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
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _connection.ActionName = ToSessionAttribute(action);
        }
        finally
        {
            _gate.Release();
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

        await _gate.WaitAsync(cancellationToken);
        try
        {
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
            Interlocked.Exchange(ref _lastRoundTrip, Environment.TickCount64);
            _gate.Release();
        }
    }

    /// <summary>
    /// Locks the session: from now on it always runs in a read-only transaction, so Oracle rejects any DML
    /// (ORA-01456). Used for profiles marked read-only (Prod by default).
    /// </summary>
    public async Task UseReadOnlySnapshotsAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
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
            _gate.Release();
        }
    }

    /// <summary>Starts a new snapshot (new read-only transaction) in a locked session; does nothing otherwise.</summary>
    public async Task RefreshSnapshotAsync(CancellationToken cancellationToken)
    {
        if (!_readOnlySnapshots)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await RestartSnapshotCoreAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Opens a transaction that may write (WP-09). Refused in a locked session.</summary>
    public async Task BeginTransactionAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
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

            _transaction = _connection.BeginTransaction();
            Transaction = new TransactionInfo(TransactionMode.ReadWrite, DateTimeOffset.Now);
        }
        finally
        {
            _gate.Release();
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
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_transaction is null || Transaction.Mode != TransactionMode.ReadWrite)
            {
                throw new InvalidOperationException("Es ist keine schreibende Transaktion offen.");
            }

            await GuardAsync(() => _transaction.CommitAsync(cancellationToken));
            await EndTransactionCoreAsync(rollback: false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Rolls the transaction back. A locked session then continues in a new snapshot; otherwise there is no
    /// transaction afterwards.
    /// </summary>
    public async Task RollbackAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
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
            _gate.Release();
        }
    }

    /// <summary>
    /// The only way to write: a single INSERT/UPDATE/DELETE (<see cref="IsWriteStatement"/>) inside an open
    /// transaction – never autocommit, never DDL. In a locked session Oracle rejects it (ORA-01456).
    /// </summary>
    /// <returns>Affected rows.</returns>
    internal async Task<int> ExecuteNonQueryAsync(string sql, IReadOnlyList<QueryParameter> parameters, CancellationToken cancellationToken)
    {
        if (!IsWriteStatement(sql))
        {
            throw new InvalidOperationException("Geschrieben wird nur mit einzelnen INSERT-, UPDATE- oder DELETE-Statements.");
        }

        await _gate.WaitAsync(cancellationToken);
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
                return await command.ExecuteNonQueryAsync(cancellationToken);
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
            Interlocked.Exchange(ref _lastRoundTrip, Environment.TickCount64);
            _gate.Release();
        }
    }

    /// <summary>Rolls back an open transaction first: nothing uncommitted may survive by accident.</summary>
    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        try
        {
            await EndTransactionCoreAsync(rollback: true);
        }
        catch (Exception ex) when (ex is OracleException or InvalidOperationException)
        {
            // Connection already gone: Oracle rolls back on its own.
        }

        await _connection.DisposeAsync();
        _gate.Dispose();
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
        command.CommandText = sql;
        foreach (var parameter in parameters)
        {
            command.Parameters.Add(ToOracleParameter(parameter));
        }

        return command;
    }

    /// <summary>Ends the current transaction (if any) and starts a read-only one. Caller holds the gate.</summary>
    private async Task RestartSnapshotCoreAsync(CancellationToken cancellationToken)
    {
        await EndTransactionCoreAsync(rollback: true);
        const string sql = "SET TRANSACTION READ ONLY";
        var transaction = _connection.BeginTransaction();
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

        await _gate.WaitAsync(cancellationToken);
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
            _gate.Release();
        }
    }

    /// <summary>Oracle errors of transaction control become <see cref="OracleStatementException"/> like those of queries.</summary>
    private static async Task GuardAsync(Func<Task> action, string sql = "(Transaktionssteuerung)")
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
        var result = new OracleParameter(parameter.Name, parameter.Value ?? DBNull.Value);
        switch (parameter.Type)
        {
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
        }

        return result;
    }
}
