using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Text;
using FerretSharp.Core.Connections;
using FerretSharp.Core.Query;
using Oracle.ManagedDataAccess.Client;

namespace FerretSharp.Core.Oracle;

/// <param name="Outputs">Output parameters by name (<c>RETURNING ROWID INTO :p_rowid</c>), as text.</param>
internal sealed record NonQueryResult(int Rows, IReadOnlyDictionary<string, object?> Outputs);

/// <summary>Values shown in <c>V$SESSION</c> (MODULE, ACTION, CLIENT_INFO) to identify who holds a session.</summary>
public sealed record SessionContext(string Module, string Action, string? ClientInfo = null);

/// <summary>
/// One long-lived Oracle connection (one per workspace). <see cref="OracleConnection"/> is not thread-safe, so all
/// commands are serialized (<see cref="ExclusiveAsync{T}"/>). <see cref="ExecuteReaderAsync{T}"/> refuses anything but
/// plain queries; writing goes only through the internal <see cref="ExecuteNonQueryAsync"/>, inside an explicit transaction.
/// Errors leave the session as <see cref="DatabaseException"/> (translated in one place, <see cref="ExclusiveAsync{T}"/>);
/// refusals as <see cref="RefusedException"/>; a disposed session cancels its callers.
/// </summary>
/// <remarks>
/// Transactions (ADR 0006, <c>OracleSession.Transactions.cs</c>): a locked session (<see cref="UseReadOnlySnapshotsAsync"/>)
/// always runs in a <c>SET TRANSACTION READ ONLY</c> transaction, so Oracle itself rejects DML. That statement must be the
/// first of an ODP.NET transaction – without one, ODP.NET commits after every statement and the read-only transaction ends
/// at once. DDL still runs in a read-only transaction (implicit commit), which is why the statement guards stay.
/// </remarks>
public sealed partial class OracleSession : IAsyncDisposable
{
    /// <summary>New snapshots tried per query before the error is reported (ORA-01466 waits 1, 2, 3 s).</summary>
    private const int MaxSnapshotRetries = 3;

    /// <summary>
    /// Characters of LONG columns fetched with the row (<c>ALL_TAB_COLUMNS.DATA_DEFAULT</c>, <c>ALL_VIEWS.TEXT</c>).
    /// Without it, ODP.NET returns no LONG data. 32767 is the driver's maximum; table data never selects LONG columns.
    /// </summary>
    internal const int LongFetchSize = 32767;

    /// <summary>How long disposing waits for a running command after cancelling it, before closing anyway.</summary>
    private static readonly TimeSpan DisposeWait = TimeSpan.FromSeconds(10);

    private readonly OracleConnection _connection;

    /// <summary>Serializes all use of <see cref="_connection"/>; never disposed (callers may still be waiting on it).</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Cancelled when disposing starts: lets go of callers at once (never disposed, like the gate).</summary>
    private readonly CancellationTokenSource _closing = new();
    private long _lastRoundTrip = Environment.TickCount64;
    private volatile bool _disposed;
    private volatile OracleCommand? _running;

    private OracleSession(OracleConnection connection) => _connection = connection;

    public string ServerVersion => _connection.ServerVersion;

    /// <summary>A single plain query (SELECT/WITH, no FOR UPDATE) – the only kind the read path runs. See <see cref="StatementGuard"/>.</summary>
    internal static bool IsReadOnlyStatement(string sql) => StatementGuard.IsQuery(sql);

    /// <summary>
    /// A single INSERT, UPDATE, DELETE or MERGE (MERGE since the SQL editor, ADR 0014). Never DDL: it would commit
    /// implicitly, also inside a read-only transaction.
    /// </summary>
    internal static bool IsWriteStatement(string sql) => StatementGuard.IsWrite(sql);

    /// <summary>A single SELECT ending in FOR UPDATE WAIT n or FOR UPDATE NOWAIT – never one that waits forever.</summary>
    internal static bool IsLockStatement(string sql) => StatementGuard.IsLock(sql);

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
        catch (OracleException ex) when (ex.Number == OracleErrorCodes.UserCancelled)
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
    public Task SetActionAsync(string action, CancellationToken cancellationToken) =>
        ExclusiveAsync(() =>
        {
            _connection.ActionName = ToSessionAttribute(action);
            return Task.CompletedTask;
        }, cancellationToken);

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
    /// In a locked session, a query that fails because the snapshot cannot serve it (<see cref="OracleErrorCodes.SnapshotUnusable"/>)
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

        return await ExclusiveAsync(async () =>
        {
            if (_readOnlySnapshots && _open is null)
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
                    _readOnlySnapshots && attempt <= MaxSnapshotRetries && ex.Oracle is { } oracle && OracleErrorCodes.SnapshotUnusable.Contains(oracle.Number))
                {
                    // A snapshot taken within about a second of the DDL is still too old for it (ORA-01466).
                    if (oracle.Number == OracleErrorCodes.DefinitionChanged)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(attempt), cancellationToken);
                    }

                    await RestartSnapshotCoreAsync(cancellationToken);
                }
            }
        }, cancellationToken);
    }

    /// <summary>
    /// Rolls back an open transaction first: nothing uncommitted may survive by accident. A running command (grid page,
    /// keep-alive ping) is cancelled and finishes before the connection closes – <see cref="OracleConnection"/> is not
    /// thread-safe. Callers – the one whose command runs and those waiting for the session – get an
    /// <see cref="OperationCanceledException"/> at once. Takes at most <see cref="DisposeWait"/>: a command that ignores the
    /// cancel (a VPN that went silent) is left behind, and its connection closes in the background.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _closing.CancelAsync();
        CancelRunningCommand();
        if (!await _gate.WaitAsync(DisposeWait))
        {
            // ODP.NET closes synchronously, and on a connection that went silent it blocks until TCP gives up – never on
            // the caller's (UI) thread. Closing ends the session; Oracle rolls back.
            _ = Task.Run(async () =>
            {
                try
                {
                    await _connection.DisposeAsync();
                }
                catch (Exception ex) when (ex is OracleException or InvalidOperationException or ObjectDisposedException)
                {
                    // the connection is gone either way
                }
            });
            return;
        }

        try
        {
            await EndTransactionQuietlyAsync();
        }
        finally
        {
            await _connection.DisposeAsync();
            _gate.Release();
        }
    }

    /// <summary>
    /// Every use of the connection: takes the gate (once the session is disposed, the call is abandoned like a cancelled
    /// one – also for callers that were already waiting), runs <paramref name="body"/>, translates Oracle errors into
    /// <see cref="DatabaseException"/> – the one place they are translated – and releases the gate.
    /// </summary>
    private async Task<T> ExclusiveAsync<T>(Func<Task<T>> body, CancellationToken cancellationToken)
    {
        if (_disposed)
        {
            throw Closed();
        }

        using (var waiting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _closing.Token))
        {
            try
            {
                await _gate.WaitAsync(waiting.Token);
            }
            catch (OperationCanceledException) when (_closing.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw Closed();
            }
        }

        Task<T>? work = null;
        var abandoned = false;
        try
        {
            if (_disposed)
            {
                throw Closed();
            }

            work = body();
            return await work.WaitAsync(_closing.Token);
        }
        catch (OperationCanceledException) when (work is { IsCompleted: false } && _closing.IsCancellationRequested)
        {
            // Disposed while the command hangs (it ignores the cancel): the caller is let go now; the command keeps the
            // gate until it ends, so nothing else touches the connection meanwhile.
            abandoned = true;
            _ = work.ContinueWith(finished =>
            {
                _ = finished.Exception; // observed: the session is gone, nobody waits for it
                Release();
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            throw Closed();
        }
        catch (Exception ex) when (_disposed && ex is not OperationCanceledException)
        {
            // Closed while the command ran: whatever the driver throws now is the closing itself, not a lost connection.
            throw new OperationCanceledException("The session was closed.", ex);
        }
        catch (Exception ex) when (OracleErrors.Translate(ex) is { } translated)
        {
            throw translated;
        }
        finally
        {
            if (!abandoned)
            {
                Release();
            }
        }
    }

    private void Release()
    {
        _running = null;
        Interlocked.Exchange(ref _lastRoundTrip, Environment.TickCount64);
        _gate.Release();
    }

    private static OperationCanceledException Closed() => new("The session was closed.");

    private Task ExclusiveAsync(Func<Task> body, CancellationToken cancellationToken) =>
        ExclusiveAsync(async () =>
        {
            await body();
            return true;
        }, cancellationToken);

    /// <summary>
    /// One command: cancelling <paramref name="cancellationToken"/> cancels it on the server (ORA-01013 becomes
    /// <see cref="OperationCanceledException"/>); other Oracle errors carry the statement. Caller holds the gate.
    /// </summary>
    private async Task<T> RunAsync<T>(
        string sql, IReadOnlyList<QueryParameter> parameters, Func<OracleCommand, CancellationToken, Task<T>> run, CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(sql, parameters);
        await using var cancelRegistration = cancellationToken.Register(static c => ((OracleCommand)c!).Cancel(), command);
        try
        {
            return await run(command, cancellationToken);
        }
        catch (OracleException ex) when (ex.Number == OracleErrorCodes.UserCancelled)
        {
            throw new OperationCanceledException("The statement was cancelled.", ex, cancellationToken);
        }
        catch (OracleException ex)
        {
            throw new OracleStatementException(sql, parameters, ex);
        }
    }

    /// <summary>A query; the reader goes to <paramref name="read"/>. Caller holds the gate.</summary>
    private Task<T> ReadCoreAsync<T>(
        string sql, IReadOnlyList<QueryParameter> parameters, Func<DbDataReader, CancellationToken, Task<T>> read, CancellationToken cancellationToken) =>
        RunAsync(sql, parameters, async (command, ct) =>
        {
            await using var reader = (OracleDataReader)await command.ExecuteReaderAsync(ct);
            reader.SuppressGetDecimalInvalidCastException = true; // NUMBER(38) exceeds decimal
            return await read(reader, ct);
        }, cancellationToken);

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
            command.Parameters.Add(OracleParameters.From(parameter));
        }

        _running = command; // cancelled by DisposeAsync
        return command;
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
}
