using System.Diagnostics;
using FerretSharp.Core.ClrModel;
using FerretSharp.Core.Resources;
using FerretSharp.Core.Schema;
using FerretSharp.Core.Workspaces;

namespace FerretSharp.Core.Connections;

public enum ConnectionStatus
{
    Disconnected,
    Connecting,
    Connected,
    Failed,
}

/// <param name="ErrorCode">e.g. <c>ORA-01017</c>; null for non-Oracle failures.</param>
public sealed record ConnectionError(string Message, string? ErrorCode);

/// <summary>How opening the explorer session ended, seen from the thread pool (<see cref="ActiveConnection.SessionOpenEnded"/>).</summary>
/// <param name="Synchronous">It had ended when the connector returned: the open blocked the calling thread, the UI thread.</param>
/// <param name="Error">Why no session opened (also a cancelled open); null if one did.</param>
public sealed record SessionOpenOutcome(TimeSpan Took, bool Synchronous, string? Error);

/// <summary>
/// The connection currently being browsed: opens the explorer session, loads the schema cache, attaches the
/// workspaces (each with its own data session, see <see cref="WorkspaceManager"/>) and records usage.
/// </summary>
public sealed class ActiveConnection(
    ConnectionManager connections, IDatabaseConnector connector, RecentConnections recent, WorkspaceManager workspaces) : IAsyncDisposable
{
    public const string ExplorerAction = "Explorer";

    /// <summary>Waiting for the previous attempt only counts as a step from this long (it is usually over at once).</summary>
    private static readonly TimeSpan NoticeableWait = TimeSpan.FromMilliseconds(100);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _stepLock = new();
    private readonly List<LoadStep> _steps = [];
    private CancellationTokenSource? _connectCts;
    private IDatabaseConnection? _connection;
    private long _stepTimestamp;

    /// <summary>Raised on every status change; may fire on a background thread.</summary>
    public event Action? Changed;

    /// <summary>
    /// Raised when <see cref="Step"/> changes; may fire on a background thread. Not <see cref="Changed"/>: on that the shell
    /// renders every mounted tab of every connection.
    /// </summary>
    public event Action? StepChanged;

    /// <summary>
    /// Raised on the thread pool when opening the explorer session has ended, also after a cancel. Not a step: the steps
    /// after it run on the caller's context, so a stuck UI thread held up the next one as well and the log could not tell it
    /// from a hanging open (2026-10-21, after sleep).
    /// </summary>
    public event Action<SessionOpenOutcome>? SessionOpenEnded;

    /// <summary>While connecting: what is being done right now, since <see cref="StepStartedAt"/>; null otherwise.</summary>
    public string? Step { get; private set; }

    public DateTimeOffset? StepStartedAt { get; private set; }

    /// <summary>When the current or last connect started.</summary>
    public DateTimeOffset? ConnectStartedAt { get; private set; }

    /// <summary>The steps of the last connect with their durations – also of a failed or cancelled one (logged by the UI).</summary>
    public IReadOnlyList<LoadStep> Steps { get; private set; } = [];

    public ConnectionStatus Status { get; private set; }

    public ConnectionProfile? Profile { get; private set; }

    public string? ServerVersion { get; private set; }

    public SchemaCache? Schema { get; private set; }

    public ConnectionError? Error { get; private set; }

    public bool IsConnected => Status == ConnectionStatus.Connected;

    /// <summary>
    /// A session of this connection was found gone (a statement or the keep-alive) while it counts as connected: its
    /// transactions are lost, the banner offers to connect again. Kept per connection (WP-24: a connection in the
    /// background can be lost while another one is shown); cleared when it connects again or disconnects.
    /// </summary>
    public DatabaseException? Lost { get; private set; }

    /// <summary>Records a lost session (first one wins); other errors are ignored. True if this changed <see cref="Lost"/>.</summary>
    public bool ReportLost(DatabaseException error)
    {
        if (!error.IsConnectionLost || !IsConnected || Lost is not null)
        {
            return false;
        }

        Lost = error;
        Changed?.Invoke();
        return true;
    }

    /// <summary>"Neu verbinden" was chosen: the banner goes away right away, before the old sessions are closed.</summary>
    public void ClearLost()
    {
        if (Lost is not null)
        {
            Lost = null;
            Changed?.Invoke();
        }
    }

    /// <summary>Closes any current connection, then opens <paramref name="profile"/>. Failures end in <see cref="ConnectionStatus.Failed"/>.</summary>
    public async Task ConnectAsync(ConnectionProfile profile, CancellationToken cancellationToken)
    {
        CancelPendingConnect();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _connectCts = cts;

        var requested = Stopwatch.GetTimestamp();
        var startedAt = DateTimeOffset.Now;
        await _gate.WaitAsync(CancellationToken.None);
        try
        {
            BeginSteps(startedAt, Stopwatch.GetElapsedTime(requested));
            // Connecting from here on, also while the old sessions close (up to 10 s each over a silent VPN).
            Set(ConnectionStatus.Connecting, profile);
            if (_connection is not null)
            {
                StartStep(ConnectionText.Step_ClosingPrevious);
            }

            await CloseAsync();

            var password = connections.GetPassword(profile.Id)
                ?? throw new DatabaseException(ConnectionText.NoPasswordSaved);

            StartStep(ConnectionText.Step_OpeningSession);
            _connection = await OpenAsync(profile, password, cts.Token);
            connections.PasswordWorked(profile.Id); // a password entered with "save" is saved only now (A11)
            var schema = new SchemaCache(_connection.Schema, profile.EffectiveSchema);
            await schema.LoadAsync(new SyncProgress<string>(StartStep), cts.Token);
            StartStep(ConnectionText.Step_RestoringWorkspaces);
            await workspaces.AttachAsync(profile, cts.Token);

            Schema = schema;
            ServerVersion = _connection.ServerVersion;
            FinishSteps();
            Set(ConnectionStatus.Connected, profile);
        }
        catch (OperationCanceledException)
        {
            FinishSteps();
            await CloseAsync();
            Set(ConnectionStatus.Disconnected, null);
            return;
        }
        catch (DatabaseException ex)
        {
            if (ex.ErrorCode == ConnectionManager.WrongPasswordCode)
            {
                connections.ForgetSessionPassword(profile.Id); // asked again next time instead of failing the same way
            }

            FinishSteps();
            await CloseAsync();
            Set(ConnectionStatus.Failed, profile, new ConnectionError(ex.Message, ex.ErrorCode));
            return;
        }
        catch (Exception ex)
        {
            // Never leave the UI stuck in "Connecting".
            FinishSteps();
            await CloseAsync();
            Set(ConnectionStatus.Failed, profile, new ConnectionError(ex.Message, null));
            return;
        }
        finally
        {
            if (ReferenceEquals(_connectCts, cts))
            {
                _connectCts = null;
            }

            _gate.Release();
        }

        try
        {
            await recent.MarkUsedAsync(profile.Id, CancellationToken.None);
        }
        catch (IOException)
        {
            // History is a convenience; a locked or read-only file must not affect the connection.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Cancels a running connect, off the calling thread: cancelling sends ODP.NET's break synchronously
    /// (<c>OracleCommand.Cancel</c>), which can stall over the VPN – the Cancel button must not freeze the window.
    /// </summary>
    public void CancelPendingConnect()
    {
        if (_connectCts is not { } cts)
        {
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // the connect ended meanwhile
            }
        });
    }

    /// <summary>
    /// Keep-alive: pings the explorer session and the open workspace sessions that have been idle for at least
    /// <paramref name="idleFor"/>. Returns the error if a session turned out to be gone; other failures are left to
    /// the next statement. Does nothing while connecting or disconnecting.
    /// </summary>
    public async Task<DatabaseException?> PingIdleSessionsAsync(TimeSpan idleFor, CancellationToken cancellationToken)
    {
        IDatabaseConnection? explorer;
        if (!_gate.Wait(0))
        {
            return null;
        }

        try
        {
            explorer = IsConnected ? _connection : null;
        }
        finally
        {
            _gate.Release();
        }

        if (explorer is null)
        {
            return null;
        }

        IDatabaseConnection[] sessions = [explorer, .. workspaces.OpenSessions()];
        foreach (var session in sessions)
        {
            try
            {
                await session.PingIfIdleAsync(idleFor, cancellationToken);
            }
            catch (DatabaseException ex) when (ex.IsConnectionLost)
            {
                return ex;
            }
            catch (Exception ex) when (ex is DatabaseException or OperationCanceledException or ObjectDisposedException)
            {
                // Cancelled (timeout), closed meanwhile (disconnect, workspace closed) or another error: not a lost connection.
            }
        }

        return null;
    }

    public async Task DisconnectAsync()
    {
        CancelPendingConnect();
        await _gate.WaitAsync();
        try
        {
            await CloseAsync();
            Set(ConnectionStatus.Disconnected, null);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        CancelPendingConnect();
        await CloseAsync();
    }

    private async Task CloseAsync()
    {
        await workspaces.DetachAsync();
        Schema = null;
        ServerVersion = null;
        if (_connection is not null)
        {
            await _connection.DisposeAsync();
            _connection = null;
        }
    }

    /// <summary>
    /// ODP.NET does not stop opening when cancelled: the TCP connect to an unreachable address runs to its timeout (15 s), and
    /// Cancel did nothing until then. Cancelling returns at once; a session that opens after all is closed right away.
    /// </summary>
    private async Task<IDatabaseConnection> OpenAsync(ConnectionProfile profile, string password, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var opening = connector.OpenAsync(profile, password, ExplorerAction, cancellationToken);
        var synchronous = opening.IsCompleted;
        _ = opening.ContinueWith(opened => SessionOpenEnded?.Invoke(new SessionOpenOutcome(
                Stopwatch.GetElapsedTime(started),
                synchronous,
                opened.IsCanceled ? "cancelled" : opened.Exception?.GetBaseException().Message)),
            CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        try
        {
            return await opening.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            _ = opening.ContinueWith(async opened =>
            {
                if (opened.IsCompletedSuccessfully)
                {
                    await opened.Result.DisposeAsync();
                }
                else
                {
                    _ = opened.Exception; // observed: a failure nobody waits for any more
                }
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
            throw;
        }
    }

    /// <summary>A new connect: its steps start now; waiting for the previous attempt counts if it was noticeable.</summary>
    private void BeginSteps(DateTimeOffset startedAt, TimeSpan waited)
    {
        lock (_stepLock)
        {
            _steps.Clear();
            ConnectStartedAt = startedAt;
            if (waited >= NoticeableWait)
            {
                _steps.Add(new LoadStep(ConnectionText.Step_WaitingForPrevious, waited));
            }
        }
    }

    /// <summary>The previous step ends here (its duration is recorded), <paramref name="step"/> starts.</summary>
    private void StartStep(string step)
    {
        lock (_stepLock)
        {
            CloseStep();
            Step = step;
            StepStartedAt = DateTimeOffset.Now;
            _stepTimestamp = Stopwatch.GetTimestamp();
        }

        StepChanged?.Invoke();
    }

    private void FinishSteps()
    {
        lock (_stepLock)
        {
            CloseStep();
            Steps = _steps.ToList();
            Step = null;
            StepStartedAt = null;
        }

        StepChanged?.Invoke();
    }

    private void CloseStep()
    {
        if (Step is { } step)
        {
            _steps.Add(new LoadStep(step, Stopwatch.GetElapsedTime(_stepTimestamp)));
        }
    }

    private void Set(ConnectionStatus status, ConnectionProfile? profile, ConnectionError? error = null)
    {
        Status = status;
        Profile = profile;
        Error = error;
        Lost = null;
        Changed?.Invoke();
    }
}
