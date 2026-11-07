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

    /// <summary>The figures since each statement was loaded into the shared pool (<c>V$SQLAREA</c>, like SQL Developer).</summary>
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

    /// <summary>The lines of the top list as the page shows them.</summary>
    public IReadOnlyList<StatementRow> Top =>
        Mode == ServerLoadMode.Window ? Tracker.Top(Window, Sort, Filter, ServerLoadService.TopCount) : SinceLoad;

    internal IDatabaseConnection? Connection { get; set; }

    internal ServerLoadMonitor? Monitor { get; set; }

    internal TaskCompletionSource Wake { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
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
            if (view.Status == ServerLoadStatus.Unavailable)
            {
                return; // the rights do not come back by themselves; „Refresh“ tries again
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

    /// <summary>Looks now instead of at the next interval; after missing rights it tries again.</summary>
    public async Task RefreshAsync(ConnectionProfile profile)
    {
        var view = For(profile.Id);
        if (view.Status == ServerLoadStatus.Unavailable)
        {
            view.Status = ServerLoadStatus.Idle;
            view.Error = null;
            await ShowAsync(profile);
            return;
        }

        view.Wake.TrySetResult();
        if (view.Mode == ServerLoadMode.SinceLoad)
        {
            await LoadSinceLoadAsync(profile);
        }
    }

    public void SetPaused(Guid profileId, bool paused)
    {
        For(profileId).Paused = paused;
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

    /// <summary>Shows the details of a statement (null: none): its whole text, figures and plan, read on the page's session.</summary>
    public async Task SelectAsync(ConnectionProfile profile, string? sqlId)
    {
        var view = For(profile.Id);
        view.Selected = sqlId;
        view.Cursor = null;
        view.CursorError = null;
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
        string? error;
        try
        {
            cursor = await Task.Run(() => connection.Schema.GetCursorByIdAsync(sqlId!, timeout.Token), CancellationToken.None);
            error = cursor is null ? ServerLoadText.ServerLoad_NotInPool : null;
        }
        catch (Exception ex) when (Failure(ex, profile, timeout) is { } failure)
        {
            error = failure;
        }

        if (view.Selected == sqlId)
        {
            (view.Cursor, view.CursorError, view.CursorLoading) = (cursor, error, false);
            Changed?.Invoke();
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
                if (view.Monitor is not { } monitor)
                {
                    await OpenAsync(view, profile, cancellationToken);
                }
                else if (!view.Paused)
                {
                    await monitor.LookAsync(_ => Set(view, ServerLoadStatus.Baseline), cancellationToken);
                    view.Sessions = monitor.Sessions;
                }

                view.Status = ServerLoadStatus.Watching;
                view.Error = null;
                view.LastLookAt = _time.GetUtcNow();
                Changed?.Invoke();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (ServerLoadUnavailableException ex)
            {
                await FailAsync(view, ServerLoadStatus.Unavailable, WithUser(ex.Message, profile));
                return;
            }
            catch (DatabaseException) when (cancellationToken.IsCancellationRequested)
            {
                return; // the session was closed under the look
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
        view.Connection = await SessionOpening.OpenAsync(
            (_, token) => connector.OpenAsync(profile, password, SessionAction, token), _time, null, cancellationToken);
        var monitor = new ServerLoadMonitor(view.Connection.ServerLoad, view.Tracker);
        await monitor.OpenAsync(_ => Set(view, ServerLoadStatus.Baseline), cancellationToken);
        (view.Monitor, view.Environment, view.Sessions) = (monitor, monitor.Environment, monitor.Sessions);
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
}
