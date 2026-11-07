using FerretSharp.Core.Connections;
using FerretSharp.Core.Query;
using FerretSharp.Core.Resources;
using FerretSharp.Core.ServerLoad;
using FerretSharp.UI.Resources;
using Microsoft.Extensions.Logging;

namespace FerretSharp.UI.State;

/// <summary>Where the server load of a connection stands.</summary>
public enum ServerLoadStatus
{
    /// <summary>Not looked at yet.</summary>
    Idle,

    Connecting,

    /// <summary>Reading the figures of every cursor once (<see cref="ServerLoadStep.Baseline"/>).</summary>
    Baseline,

    Watching,

    /// <summary>The user may not read the V$ views: nothing more is tried (<see cref="ServerLoadView.Error"/>).</summary>
    Unavailable,

    /// <summary>The latest look failed (<see cref="ServerLoadView.Error"/>); the next one opens a new session.</summary>
    Failed,
}

/// <summary>Which top list the page shows.</summary>
public enum ServerLoadMode
{
    /// <summary>What the statements did while the page looked (<see cref="ServerLoadTracker"/>).</summary>
    Window,

    /// <summary>The figures since each statement was loaded into the shared pool (<c>V$SQLSTATS</c>, like SQL Developer).</summary>
    SinceLoad,
}

/// <summary>
/// The server load page of one connection (WP-75): what was seen, and how the user looks at it. Kept while the app runs, so
/// the history and „since opened“ survive leaving the page; the session behind it does not.
/// </summary>
public sealed class ServerLoadView(Guid profileId)
{
    public Guid ProfileId => profileId;

    public ServerLoadTracker Tracker { get; } = new();

    public ServerLoadStatus Status { get; internal set; }

    public string? Error { get; internal set; }

    public ServerEnvironment? Environment { get; internal set; }

    public IReadOnlyList<ActiveSession> Sessions { get; internal set; } = [];

    /// <summary>When the latest look ended (this computer's clock, for „12 s ago“).</summary>
    public DateTimeOffset? LastLookAt { get; internal set; }

    /// <summary>The user paused the looks: the page stays as it is.</summary>
    public bool Paused { get; internal set; }

    public ServerLoadMode Mode { get; set; }

    /// <summary>The window of the top list; null: since the page was first opened.</summary>
    public TimeSpan? Window { get; set; }

    public TopStatementSort Sort { get; set; }

    public StatementFilter Filter { get; set; } = StatementFilter.None;

    /// <summary>The list since loading, as read last (<see cref="ServerLoadService.LoadSinceLoadAsync"/>).</summary>
    public IReadOnlyList<StatementRow> SinceLoad { get; internal set; } = [];

    public bool SinceLoadLoading { get; internal set; }

    public string? SinceLoadError { get; internal set; }

    /// <summary>The statement whose details are shown.</summary>
    public string? Selected { get; internal set; }

    /// <summary>Figures and plan of <see cref="Selected"/>; null while loading, after an error, or when it left the shared pool.</summary>
    public CursorInfo? Cursor { get; internal set; }

    public bool CursorLoading { get; internal set; }

    public string? CursorError { get; internal set; }

    /// <summary>
    /// The whole text of <see cref="Selected"/> (<c>SQL_FULLTEXT</c>), also when its cursor is gone; null while loading or when
    /// it is gone from <c>V$SQLSTATS</c> too – the list's text may then be cut (<see cref="StatementInfo.MayBeCut"/>).
    /// </summary>
    public string? FullText { get; internal set; }

    /// <summary>The loop ended by itself (no rights, login refused): only „Refresh“ tries again.</summary>
    public bool Stopped { get; internal set; }

    /// <summary>The lines of the top list as the page shows them.</summary>
    public IReadOnlyList<StatementRow> Top =>
        Mode == ServerLoadMode.Window ? Tracker.Top(Window, Sort, Filter, ServerLoadService.TopCount) : SinceLoad;

    internal IDatabaseConnection? Connection { get; set; }

    internal ServerLoadMonitor? Monitor { get; set; }

    internal TaskCompletionSource Wake { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private int _lookOnce;

    /// <summary>„Refresh“ while paused: the next round looks once.</summary>
    internal void LookOnce() => Interlocked.Exchange(ref _lookOnce, 1);

    internal bool TakeLookOnce() => Interlocked.Exchange(ref _lookOnce, 0) == 1;
}

/// <summary>
/// Looks at the server of the connection the server load page shows (WP-75, ADR 0045): a session of its own (ACTION
/// <see cref="SessionAction"/>, never a workspace's or the explorer's, which work one call after the other), a look every
/// <see cref="Interval"/> while the page is shown, the session closed when it is left. One page at a time; the state of each
/// connection stays (<see cref="ServerLoadView"/>).
/// </summary>
/// <remarks>
/// Looks run on the pool; <see cref="Changed"/> comes from there. SQL texts of other sessions stay in memory: nothing here
/// logs a statement, only the error of a look.
/// </remarks>
public sealed class ServerLoadService(
    ConnectionManager connections, IDatabaseConnector connector, ILogger<ServerLoadService> logger, TimeProvider? timeProvider = null) : IAsyncDisposable
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    /// <summary>Looking up one statement (text, figures, plan) gives up after this.</summary>
    public static readonly TimeSpan LookupTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// A look (or opening the session with the baseline) gives up after this (review of WP-75): over a silent VPN it would
    /// hang until TCP gives up. Then the session is closed – a call hanging on it ends at once – and the next look opens anew.
    /// </summary>
    public static readonly TimeSpan LookTimeout = TimeSpan.FromMinutes(2);

    /// <summary>MODULE stays FerretSharp; ACTION tells a DBA what this session is for (ASCII only, <c>.claude/rules/oracle.md</c>).</summary>
    public const string SessionAction = "Serverlast";

    public const int TopCount = 50;

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly Lock _lock = new();
    private readonly SemaphoreSlim _switching = new(1, 1);
    private readonly Dictionary<Guid, ServerLoadView> _views = [];
    private (ServerLoadView View, CancellationTokenSource Stop, Task Loop)? _running;

    public event Action? Changed;

    public ServerLoadView For(Guid profileId)
    {
        lock (_lock)
        {
            if (!_views.TryGetValue(profileId, out var view))
            {
                _views[profileId] = view = new ServerLoadView(profileId);
            }

            return view;
        }
    }

    /// <summary>The page shows <paramref name="profile"/>: look at its server from now on, and stop looking at another one.</summary>
    public async Task ShowAsync(ConnectionProfile profile)
    {
        await _switching.WaitAsync();
        try
        {
            var view = For(profile.Id);
            if (_running is { } running && running.View == view && !running.Loop.IsCompleted)
            {
                return; // a loop that ended (missing rights) is replaced below
            }

            await StopAsync();
            if (view.Stopped)
            {
                return; // rights and a refused login do not mend by themselves; „Refresh“ tries again
            }

            var stop = new CancellationTokenSource();
            _running = (view, stop, Task.Run(() => RunAsync(view, profile, stop.Token), CancellationToken.None));
        }
        finally
        {
            _switching.Release();
        }
    }

    /// <summary>
    /// The page of <paramref name="profileId"/> was left: no more looks, the session closes. What was seen stays. A page for
    /// another connection may have started already (it is built before the old one goes): that one keeps looking.
    /// </summary>
    public async Task HideAsync(Guid profileId)
    {
        await _switching.WaitAsync();
        try
        {
            if (_running?.View.ProfileId == profileId)
            {
                await StopAsync();
            }
        }
        finally
        {
            _switching.Release();
        }
    }

    /// <summary>
    /// Looks now instead of at the next interval, also once while paused; after missing rights or a refused login it tries
    /// again.
    /// </summary>
    public async Task RefreshAsync(ConnectionProfile profile)
    {
        var view = For(profile.Id);
        if (view.Stopped)
        {
            (view.Stopped, view.Status, view.Error) = (false, ServerLoadStatus.Idle, null);
            await ShowAsync(profile);
            return;
        }

        view.LookOnce();
        view.Wake.TrySetResult();
        if (view.Mode == ServerLoadMode.SinceLoad)
        {
            await LoadSinceLoadAsync(profile);
        }
    }

    /// <summary>Pauses the looks; resuming looks at once.</summary>
    public void SetPaused(Guid profileId, bool paused)
    {
        var view = For(profileId);
        view.Paused = paused;
        if (!paused)
        {
            view.Wake.TrySetResult();
        }

        Changed?.Invoke();
    }

    /// <summary>Reads the list since loading with the view's sort and filter, on the page's session.</summary>
    public async Task LoadSinceLoadAsync(ConnectionProfile profile)
    {
        var view = For(profile.Id);
        if (view.Connection is not { } connection)
        {
            view.SinceLoadError = view.Status == ServerLoadStatus.Unavailable ? view.Error : ServerLoadText.ServerLoad_NotWatching;
            Changed?.Invoke();
            return;
        }

        view.SinceLoadLoading = true;
        view.SinceLoadError = null;
        Changed?.Invoke();
        using var timeout = new CancellationTokenSource(LookupTimeout);
        try
        {
            var (sort, filter) = (view.Sort, view.Filter);
            view.SinceLoad = await Task.Run(() => connection.ServerLoad.ReadTopSinceLoadAsync(sort, filter, TopCount, timeout.Token), CancellationToken.None);
        }
        catch (Exception ex) when (Failure(ex, profile, timeout) is { } error)
        {
            view.SinceLoadError = error;
        }
        finally
        {
            view.SinceLoadLoading = false;
            Changed?.Invoke();
        }
    }

    /// <summary>
    /// Shows the details of a statement (null: none): figures and plan, and its whole text – read on its own when the cursor
    /// left the library cache (review of WP-75: the list's text may be cut at 1000 characters).
    /// </summary>
    public async Task SelectAsync(ConnectionProfile profile, string? sqlId)
    {
        var view = For(profile.Id);
        (view.Selected, view.Cursor, view.FullText, view.CursorError) = (sqlId, null, null, null);
        view.CursorLoading = sqlId is not null && view.Connection is not null;
        if (sqlId is not null && view.Connection is null)
        {
            view.CursorError = ServerLoadText.ServerLoad_NotWatching;
        }

        Changed?.Invoke();
        if (!view.CursorLoading || view.Connection is not { } connection)
        {
            return;
        }

        using var timeout = new CancellationTokenSource(LookupTimeout);
        CursorInfo? cursor = null;
        string? fullText = null, error = null;
        try
        {
            cursor = await Task.Run(() => connection.Schema.GetCursorByIdAsync(sqlId!, timeout.Token), CancellationToken.None);
            fullText = cursor?.Plan.Sql
                       ?? await Task.Run(() => connection.ServerLoad.ReadFullTextAsync(sqlId!, timeout.Token), CancellationToken.None);
            if (fullText?.Length == StatementInfo.TextLength)
            {
                fullText = null; // as long as V$SQLSTATS keeps of a cursor gone from V$SQL: maybe cut, never as if whole
            }
            error = cursor is null ? ServerLoadText.ServerLoad_NotInPool : null;
        }
        catch (Exception ex) when (ex is OperationCanceledException or DatabaseException or RefusedException)
        {
            error = Failure(ex, profile, timeout); // null for a cancel that is not the timeout's: the session was closed
        }
        finally
        {
            // Every end clears the spinner (review of WP-75: a cancel left it turning) – unless another statement was chosen meanwhile.
            if (view.Selected == sqlId)
            {
                (view.Cursor, view.FullText, view.CursorError, view.CursorLoading) = (cursor, fullText, error, false);
                Changed?.Invoke();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _switching.WaitAsync();
        try
        {
            await StopAsync();
        }
        finally
        {
            _switching.Release();
        }

        _switching.Dispose();
    }

    private async Task StopAsync()
    {
        if (_running is not { } running)
        {
            return;
        }

        _running = null;
        await running.Stop.CancelAsync();
        await CloseAsync(running.View); // a look hanging on a silent line ends at once (OracleSession.DisposeAsync)
        await running.Loop;
        running.Stop.Dispose();
        await CloseAsync(running.View); // a session the loop opened meanwhile
        if (running.View.Status is ServerLoadStatus.Connecting or ServerLoadStatus.Baseline or ServerLoadStatus.Watching)
        {
            running.View.Status = ServerLoadStatus.Idle;
        }

        Changed?.Invoke();
    }

    private async Task RunAsync(ServerLoadView view, ConnectionProfile profile, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                // Paused: no look, and nothing claims one – „As of“ stays at the latest real look (review of WP-75). The wish to
                // look once is used up every round: one left from a „Refresh“ before pausing must not look later (E2E).
                var once = view.TakeLookOnce();
                if (view.Monitor is null || !view.Paused || once)
                {
                    await WithinLimitAsync(view, token => view.Monitor is { } monitor ? LookAsync(view, monitor, token) : OpenAsync(view, profile, token),
                        cancellationToken);
                    view.Status = ServerLoadStatus.Watching;
                    view.Error = null;
                    view.LastLookAt = _time.GetUtcNow();
                    Changed?.Invoke();
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (LookTimedOutException)
            {
                (view.Status, view.Error) = (ServerLoadStatus.Failed, TextFormat.Format(ServerLoadText.ServerLoad_LookTimedOut, (int)LookTimeout.TotalSeconds));
                Changed?.Invoke();
            }
            catch (ServerLoadUnavailableException ex)
            {
                await FailAsync(view, ServerLoadStatus.Unavailable, WithUser(ex.Message, profile));
                view.Stopped = true;
                return;
            }
            catch (DatabaseException) when (cancellationToken.IsCancellationRequested)
            {
                return; // the session was closed under the look
            }
            catch (LoginRefusedException ex)
            {
                // Never log in again by itself (review of WP-75): every refused login counts towards FAILED_LOGIN_ATTEMPTS,
                // every 30 s the account would be locked in minutes (.claude/rules/connections.md).
                logger.LogWarning(ex.InnerException, "Opening the server load session was refused");
                await FailAsync(view, ServerLoadStatus.Failed, ex.Message);
                view.Stopped = true;
                return;
            }
            catch (DatabaseException ex)
            {
                logger.LogWarning(ex, "A look at the server load failed");
                await FailAsync(view, ServerLoadStatus.Failed, ex.Message);
            }

            view.Wake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                await Task.WhenAny(Task.Delay(Interval, _time, cancellationToken), view.Wake.Task);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task OpenAsync(ServerLoadView view, ConnectionProfile profile, CancellationToken cancellationToken)
    {
        if (connections.GetPassword(profile.Id) is not { } password)
        {
            throw new DatabaseException(TextFormat.Format(CompareText.NoPasswordFor, profile.Name));
        }

        Set(view, ServerLoadStatus.Connecting);
        try
        {
            view.Connection = await SessionOpening.OpenAsync(
                (_, token) => connector.OpenAsync(profile, password, SessionAction, token), _time, null, cancellationToken);
        }
        catch (DatabaseException ex) when (ex is not SessionOpenTimeoutException && !ReconnectPolicy.IsNetworkError(ex.ErrorCode))
        {
            throw new LoginRefusedException(ex);
        }

        var monitor = new ServerLoadMonitor(view.Connection.ServerLoad, view.Tracker);
        await monitor.OpenAsync(_ => Set(view, ServerLoadStatus.Baseline), cancellationToken);
        (view.Monitor, view.Environment, view.Sessions) = (monitor, monitor.Environment, monitor.Sessions);
    }

    private async Task LookAsync(ServerLoadView view, ServerLoadMonitor monitor, CancellationToken cancellationToken)
    {
        await monitor.LookAsync(_ => Set(view, ServerLoadStatus.Baseline), cancellationToken);
        view.Sessions = monitor.Sessions;
    }

    /// <summary>
    /// Runs a look within <see cref="LookTimeout"/>. Past it the look is cancelled and the session closed: ODP.NET may wait
    /// for a break on a silent line until TCP gives up, closing ends the call at once. A session opened meanwhile is closed too.
    /// </summary>
    /// <exception cref="LookTimedOutException">The look took too long; the session is closed.</exception>
    private async Task WithinLimitAsync(ServerLoadView view, Func<CancellationToken, Task> look, CancellationToken cancellationToken)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var work = look(limit.Token);
        try
        {
            await work.WaitAsync(LookTimeout, _time, cancellationToken);
        }
        catch (TimeoutException)
        {
            await limit.CancelAsync();
            await CloseAsync(view);
            try
            {
                await work;
            }
            catch (Exception ex) when (ex is OperationCanceledException or DatabaseException)
            {
                // ended by the close
            }

            await CloseAsync(view);
            throw new LookTimedOutException();
        }
    }

    private async Task FailAsync(ServerLoadView view, ServerLoadStatus status, string error)
    {
        await CloseAsync(view);
        (view.Status, view.Error) = (status, error);
        Changed?.Invoke();
    }

    private async Task CloseAsync(ServerLoadView view)
    {
        var connection = view.Connection;
        (view.Connection, view.Monitor) = (null, null);
        if (connection is not null)
        {
            try
            {
                await Task.Run(() => connection.DisposeAsync().AsTask(), CancellationToken.None); // ODP.NET closes synchronously: never on the UI thread
            }
            catch (Exception ex) when (ex is DatabaseException or InvalidOperationException)
            {
                logger.LogDebug(ex, "Closing the server load session failed");
            }
        }
    }

    private void Set(ServerLoadView view, ServerLoadStatus status)
    {
        view.Status = status;
        Changed?.Invoke();
    }

    /// <summary>What to show for a failed read; null for a cancel that is not the timeout's (the page was left).</summary>
    private string? Failure(Exception ex, ConnectionProfile profile, CancellationTokenSource timeout)
    {
        switch (ex)
        {
            case OperationCanceledException when timeout.IsCancellationRequested:
                return ServerLoadText.ServerLoad_TimedOut;
            case OperationCanceledException:
                return null;
            case RefusedException or ServerLoadUnavailableException:
                return WithUser(ex.Message, profile);
            case DatabaseException:
                logger.LogWarning(ex, "Reading from the server load session failed");
                return ex.Message;
            default:
                return null;
        }
    }

    private static string WithUser(string message, ConnectionProfile profile) =>
        message.Replace("<user>", profile.User.ToUpperInvariant(), StringComparison.Ordinal);

    /// <summary>Opening the session was refused for another reason than the network (wrong password, locked account).</summary>
    private sealed class LoginRefusedException(DatabaseException inner) : DatabaseException(inner.Message, inner.ErrorCode, inner);

    private sealed class LookTimedOutException() : Exception("The look at the server load timed out.");
}
