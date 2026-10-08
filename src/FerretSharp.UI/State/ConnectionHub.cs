using FerretSharp.Core.ClrModel;
using FerretSharp.Core.Connections;
using FerretSharp.Core.Workspaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FerretSharp.UI.State;

/// <summary>
/// One open connection with everything that belongs to it (WP-24): its explorer session and schema
/// (<see cref="ActiveConnection"/>), its workspaces with their sessions and transactions, the C# model, how values are
/// shown and the LINQ host. A DI scope of its own; components get these through cascading values, not <c>@inject</c>.
/// </summary>
public sealed class ConnectionScope : IAsyncDisposable
{
    private readonly AsyncServiceScope _scope;
    private readonly ConnectionProfile? _openedWith;
    private readonly ClrActivityLog? _activity;

    internal ConnectionScope(AsyncServiceScope scope, ConnectionProfile? profile)
    {
        _scope = scope;
        _openedWith = profile;
        Active = scope.ServiceProvider.GetRequiredService<ActiveConnection>();
        Workspaces = scope.ServiceProvider.GetRequiredService<WorkspaceManager>();
        Models = scope.ServiceProvider.GetRequiredService<ClrModelManager>();
        Presentations = scope.ServiceProvider.GetRequiredService<PresentationService>();
        Linq = scope.ServiceProvider.GetRequiredService<LinqConsoleService>();
        if (profile is not null && scope.ServiceProvider.GetService<ILoggerFactory>() is { } loggers)
        {
            _activity = new ClrActivityLog(Models, Linq, loggers.CreateLogger<ClrActivityLog>(), profile.Name);
        }
    }

    /// <summary>
    /// The profile it is connected with – after an edit and reconnect the new one (Prod masking must follow it) – or the
    /// one it was opened with while not connected; null for <see cref="ConnectionHub.Idle"/>.
    /// </summary>
    public ConnectionProfile? Profile => Active.Profile ?? _openedWith;

    public Guid Id => _openedWith?.Id ?? Guid.Empty;

    public ActiveConnection Active { get; }

    public WorkspaceManager Workspaces { get; }

    public ClrModelManager Models { get; }

    public PresentationService Presentations { get; }

    public LinqConsoleService Linq { get; }

    /// <summary>The session was found gone (query or keep-alive); the banner offers to reconnect once it is shown.</summary>
    public DatabaseException? Lost => Active.Lost;

    /// <summary>When it went to the background; null while shown.</summary>
    public DateTimeOffset? HiddenSince { get; internal set; }

    /// <summary>Workspaces of this connection with an open transaction and their uncommitted writes (for the hint while hidden).</summary>
    public int UncommittedActions => Workspaces.Open.Sum(w => Workspaces.ActionsOf(w.Id).Count);

    /// <summary>Disconnects (saving the workspaces, closing the sessions) and disposes the scope's services.</summary>
    public async ValueTask DisposeAsync()
    {
        _activity?.Dispose();
        await Active.DisconnectAsync();
        await _scope.DisposeAsync();
    }
}

/// <summary>
/// The connections open at the same time (WP-24, decision of the user: several open, one shown). Opening a connection
/// that is already open only shows it; switching never disconnects. <see cref="Previous"/> is the connection shown before
/// the current one (Alt+O jumps there). A LINQ host of a connection in the background for <see cref="LinqIdleTime"/> is
/// ended (it holds a .NET process with the model) and starts again when needed.
/// </summary>
public sealed class ConnectionHub(IServiceScopeFactory scopes, TimeProvider? timeProvider = null, ILogger<ConnectionHub>? logger = null)
    : IOpenConnections, IAsyncDisposable
{
    public static readonly TimeSpan LinqIdleTime = TimeSpan.FromMinutes(15);

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly Lock _lock = new();
    private List<ConnectionScope> _open = [];
    private ITimer? _timer;
    private ConnectionScope? _idle;

    /// <summary>Opened, closed or switched; fires on the caller's thread.</summary>
    public event Action? Changed;

    /// <summary>In the order they were opened.</summary>
    public IReadOnlyList<ConnectionScope> Open
    {
        get
        {
            lock (_lock)
            {
                return _open;
            }
        }
    }

    public ConnectionScope? Current { get; private set; }

    /// <summary>
    /// What components see when no connection is open: a scope that never connects (status Disconnected, no
    /// workspaces, no model), so they always get cascaded values.
    /// </summary>
    public ConnectionScope Idle => _idle ??= new ConnectionScope(scopes.CreateAsyncScope(), null);

    /// <summary>The scope the page shows: <see cref="Current"/>, or <see cref="Idle"/>.</summary>
    public ConnectionScope Shown => Current ?? Idle;

    /// <summary>The connection shown before <see cref="Current"/>, if it is still open.</summary>
    public ConnectionScope? Previous { get; private set; }

    IReadOnlyList<ActiveConnection> IOpenConnections.All => Open.Select(s => s.Active).ToList();

    public ConnectionScope? Find(Guid profileId) => Open.FirstOrDefault(s => s.Id == profileId);

    /// <summary>The connection a workspace belongs to.</summary>
    public ConnectionScope? OwnerOf(Guid workspaceId) => Open.FirstOrDefault(s => s.Workspaces.Find(workspaceId) is not null);

    /// <summary>
    /// The connection of a workspace for an action on it (lifecycle, editing). A workspace closed meanwhile falls back to
    /// the shown connection, where the action then finds no workspace and ends quietly (<see cref="WorkspaceClosedException"/>).
    /// </summary>
    public ConnectionScope ScopeOfWorkspace(Guid workspaceId) => OwnerOf(workspaceId) ?? Shown;

    /// <summary>The connection of a workspace's tabs: by its connection id, without searching every workspace list.</summary>
    public ConnectionScope ScopeOf(WorkspaceTabs workspace) => Find(workspace.ConnectionId) ?? ScopeOfWorkspace(workspace.WorkspaceId);

    public bool IsOpen(Guid profileId) => Find(profileId) is not null;

    /// <summary>
    /// Shows the connection, opening a scope for it first if needed. Synchronous and meant for the UI thread (R3b): the list
    /// of open connections and <see cref="Current"/> change only there, while the UI renders them. Connecting is the
    /// caller's next step, in the background, if <see cref="NeedsConnect"/>.
    /// </summary>
    public ConnectionScope Show(ConnectionProfile profile)
    {
        ConnectionScope scope;
        lock (_lock)
        {
            if (_open.FirstOrDefault(s => s.Id == profile.Id) is { } open)
            {
                scope = open;
            }
            else
            {
                scope = new ConnectionScope(scopes.CreateAsyncScope(), profile);
                _open = [.. _open, scope];
            }
        }

        Show(scope);
        _timer ??= _time.CreateTimer(_ => _ = StopIdleLinqHostsQuietlyAsync(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        return scope;
    }

    /// <summary>Not connected yet, or the last attempt failed: the caller of <see cref="Show(ConnectionProfile)"/> connects it.</summary>
    public static bool NeedsConnect(ConnectionScope scope) => scope.Active.Status is ConnectionStatus.Failed or ConnectionStatus.Disconnected;

    /// <summary><see cref="Show(ConnectionProfile)"/>, then connects if needed (failures end in <see cref="ConnectionStatus.Failed"/>).</summary>
    public async Task OpenAsync(ConnectionProfile profile)
    {
        var scope = Show(profile);
        if (NeedsConnect(scope))
        {
            await scope.Active.ConnectAsync(profile, CancellationToken.None);
        }
    }

    /// <summary>Shows an open connection; the one shown before becomes <see cref="Previous"/>.</summary>
    public void Show(ConnectionScope scope)
    {
        if (Current == scope)
        {
            return;
        }

        if (Current is { } before)
        {
            before.HiddenSince = _time.GetUtcNow();
            Previous = before;
        }

        scope.HiddenSince = null;
        Current = scope;
        Changed?.Invoke();
    }

    /// <summary>Back to the connection shown before (Alt+O); false if there is none.</summary>
    public bool ShowPrevious()
    {
        if (Previous is not { } previous || !Open.Contains(previous))
        {
            return false;
        }

        Show(previous);
        return true;
    }

    /// <summary>
    /// Disconnects and forgets a connection; the caller asked about uncommitted changes before. If it was shown, the
    /// previous one (or the last opened) is shown instead – or none.
    /// </summary>
    public async Task CloseAsync(ConnectionScope scope)
    {
        lock (_lock)
        {
            _open = _open.Where(s => s != scope).ToList();
        }

        if (Previous == scope)
        {
            Previous = null;
        }

        if (Current == scope)
        {
            Current = null;
            var next = Previous ?? Open.LastOrDefault();
            Previous = null;
            if (next is not null)
            {
                Show(next);
            }
        }

        Changed?.Invoke();
        await scope.DisposeAsync();
    }

    /// <summary>Ends the LINQ hosts of connections that have been in the background for <see cref="LinqIdleTime"/>.</summary>
    public async Task StopIdleLinqHostsAsync()
    {
        var now = _time.GetUtcNow();
        foreach (var scope in Open.Where(s => s.HiddenSince is { } since && now - since >= LinqIdleTime))
        {
            await scope.Linq.StopIdleAsync();
        }
    }

    /// <summary>The timer's call: a connection closed meanwhile (its services disposed) must not end in an unobserved exception.</summary>
    private async Task StopIdleLinqHostsQuietlyAsync()
    {
        try
        {
            await StopIdleLinqHostsAsync();
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException or IOException)
        {
            logger?.LogDebug(ex, "Stopping idle LINQ hosts failed");
        }
    }

    /// <summary>
    /// On exit: first saves the workspaces of every connection, then closes all of them at once. Closing one session can
    /// block for seconds on a VPN that went silent; the app waits only so long, and the workspaces of the connections after
    /// it must not depend on that.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        _timer?.Dispose();
        var open = Open;
        await Task.WhenAll(open.Select(s => s.Workspaces.FlushAsync()));
        await Task.WhenAll(open.Select(s => s.DisposeAsync().AsTask()));

        lock (_lock)
        {
            _open = [];
        }

        Current = Previous = null;
        if (_idle is not null)
        {
            await _idle.DisposeAsync();
        }
    }
}
