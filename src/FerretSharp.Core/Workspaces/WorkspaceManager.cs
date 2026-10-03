using System.Text.Json;
using FerretSharp.Core.Connections;
using FerretSharp.Core.Data;

namespace FerretSharp.Core.Workspaces;

/// <summary>
/// Workspaces of the connected profile: which are open, which is active, and one database session per open
/// workspace (ACTION = workspace name), opened on first use. Changes are saved shortly after they happen
/// (<see cref="SaveDelay"/>) and immediately on structural changes (create, close, reopen, detach).
/// </summary>
public sealed class WorkspaceManager(
    IWorkspaceStore store, ConnectionManager connections, IDatabaseConnector connector, TimeProvider? timeProvider = null) : IAsyncDisposable
{
    public static readonly TimeSpan SaveDelay = TimeSpan.FromSeconds(1);

    private const string DefaultNamePrefix = "Workspace ";

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly Lock _lock = new();
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly Dictionary<Guid, Task<IDatabaseConnection>> _sessions = [];
    private readonly HashSet<Guid> _dirty = [];
    private List<Workspace> _workspaces = [];
    private Guid? _activeId;
    private ConnectionProfile? _profile;
    private CancellationTokenSource _lifetime = new();
    private ITimer? _saveTimer;

    /// <summary>Raised when the set of workspaces, their names or the active one changed; may fire on a background thread.</summary>
    public event Action? Changed;

    public ConnectionProfile? Profile
    {
        get
        {
            lock (_lock)
            {
                return _profile;
            }
        }
    }

    /// <summary>Open workspaces in bar order.</summary>
    public IReadOnlyList<Workspace> Open
    {
        get
        {
            lock (_lock)
            {
                return OpenOrdered();
            }
        }
    }

    /// <summary>Closed workspaces, most recently used first.</summary>
    public IReadOnlyList<Workspace> Closed
    {
        get
        {
            lock (_lock)
            {
                return _workspaces.Where(w => !w.IsOpen).OrderByDescending(w => w.LastActive).ToList();
            }
        }
    }

    public Workspace? Active
    {
        get
        {
            lock (_lock)
            {
                return _workspaces.FirstOrDefault(w => w.Id == _activeId);
            }
        }
    }

    /// <summary>Workspace files that could not be read on attach.</summary>
    public IReadOnlyList<string> LoadErrors { get; private set; } = [];

    /// <summary>Set while a workspace could not be saved; cleared after the next successful save.</summary>
    public string? SaveError { get; private set; }

    /// <summary>Workspace sessions that are open (for the keep-alive); sessions still opening or failed are left out.</summary>
    public IReadOnlyList<IDatabaseConnection> OpenSessions()
    {
        lock (_lock)
        {
            return _sessions.Values.Where(s => s.IsCompletedSuccessfully).Select(s => s.Result).ToList();
        }
    }

    public Workspace? Find(Guid workspaceId)
    {
        lock (_lock)
        {
            return _workspaces.FirstOrDefault(w => w.Id == workspaceId);
        }
    }

    /// <summary>
    /// Loads the workspaces of <paramref name="profile"/>. Makes sure one is open: reopens the most recently used one,
    /// or creates "Workspace 1". The most recently active open workspace becomes active.
    /// </summary>
    public async Task AttachAsync(ConnectionProfile profile, CancellationToken cancellationToken)
    {
        await DetachAsync();
        var result = await store.LoadAsync(profile.Id, cancellationToken);
        lock (_lock)
        {
            _profile = profile;
            _lifetime = new CancellationTokenSource();
            _workspaces = result.Workspaces.ToList();
            LoadErrors = result.Errors;

            if (!_workspaces.Any(w => w.IsOpen))
            {
                var workspace = _workspaces.MaxBy(w => w.LastActive) is { } recent
                    ? recent with { IsOpen = true, Order = NextOrder() }
                    : NewWorkspace(NextName());
                Put(workspace);
            }

            _activeId = _workspaces.Where(w => w.IsOpen).MaxBy(w => w.LastActive)!.Id;
        }

        await FlushAsync();
        RaiseChanged();
    }

    /// <summary>Saves pending changes and closes all workspace sessions.</summary>
    public async Task DetachAsync()
    {
        List<Task<IDatabaseConnection>> sessions;
        lock (_lock)
        {
            if (_profile is null)
            {
                return;
            }

            _lifetime.Cancel();
            sessions = _sessions.Values.ToList();
            _sessions.Clear();
        }

        await FlushAsync();
        foreach (var session in sessions)
        {
            await DisposeSessionAsync(session);
        }

        lock (_lock)
        {
            _profile = null;
            _workspaces = [];
            _activeId = null;
            _dirty.Clear();
            LoadErrors = [];
            _lifetime.Dispose();
        }

        RaiseChanged();
    }

    /// <summary>Creates, opens and activates a workspace. <paramref name="name"/> null picks "Workspace N".</summary>
    public async Task<Workspace> CreateAsync(string? name = null)
    {
        Workspace workspace;
        lock (_lock)
        {
            RequireAttached();
            workspace = NewWorkspace(name is null ? NextName() : Workspace.NormalizeName(name) ?? throw InvalidName());
            Put(workspace);
            _activeId = workspace.Id;
        }

        await FlushAsync();
        RaiseChanged();
        return workspace;
    }

    public void Activate(Guid workspaceId)
    {
        lock (_lock)
        {
            if (Get(workspaceId) is not { IsOpen: true } workspace || _activeId == workspaceId)
            {
                return;
            }

            Put(workspace with { LastActive = _time.GetUtcNow() });
            _activeId = workspaceId;
            ScheduleSave();
        }

        RaiseChanged();
    }

    public void Rename(Guid workspaceId, string name)
    {
        var normalized = Workspace.NormalizeName(name) ?? throw InvalidName();
        Task<IDatabaseConnection>? session;
        lock (_lock)
        {
            if (Get(workspaceId) is not { } workspace || workspace.Name == normalized)
            {
                return;
            }

            Put(workspace with { Name = normalized });
            ScheduleSave();
            session = _sessions.GetValueOrDefault(workspaceId);
        }

        if (session is not null)
        {
            _ = SetActionAsync(session, normalized);
        }

        RaiseChanged();
    }

    /// <summary>Saves and closes the workspace and its session; the neighbor becomes active. The last open workspace stays open.</summary>
    public async Task CloseAsync(Guid workspaceId)
    {
        Task<IDatabaseConnection>? session;
        lock (_lock)
        {
            var open = OpenOrdered();
            var index = open.FindIndex(w => w.Id == workspaceId);
            if (index < 0)
            {
                return;
            }

            if (open.Count == 1)
            {
                throw new InvalidOperationException("Der letzte offene Workspace kann nicht geschlossen werden.");
            }

            Put(open[index] with { IsOpen = false });
            if (_activeId == workspaceId)
            {
                var next = open[index + 1 < open.Count ? index + 1 : index - 1];
                Put(next with { LastActive = _time.GetUtcNow() });
                _activeId = next.Id;
            }

            _sessions.Remove(workspaceId, out session);
        }

        await FlushAsync();
        RaiseChanged();
        if (session is not null)
        {
            await DisposeSessionAsync(session);
        }
    }

    /// <summary>Opens a closed workspace again (at the end of the bar) and activates it.</summary>
    public async Task ReopenAsync(Guid workspaceId)
    {
        lock (_lock)
        {
            if (Get(workspaceId) is not { IsOpen: false } workspace)
            {
                return;
            }

            Put(workspace with { IsOpen = true, Order = NextOrder(), LastActive = _time.GetUtcNow() });
            _activeId = workspaceId;
        }

        await FlushAsync();
        RaiseChanged();
    }

    /// <summary>Deletes a closed workspace for good.</summary>
    public async Task DeleteAsync(Guid workspaceId)
    {
        lock (_lock)
        {
            if (Get(workspaceId) is not { } workspace)
            {
                return;
            }

            if (workspace.IsOpen)
            {
                throw new InvalidOperationException("Nur geschlossene Workspaces können gelöscht werden.");
            }

            _workspaces.Remove(workspace);
            _dirty.Remove(workspaceId);
        }

        await store.DeleteAsync(workspaceId, CancellationToken.None);
        RaiseChanged();
    }

    /// <summary>Removes the stored workspaces of a deleted connection.</summary>
    public Task DeleteForConnectionAsync(Guid connectionId) => store.DeleteForConnectionAsync(connectionId, CancellationToken.None);

    /// <summary>Records the current tabs of a workspace; saved after <see cref="SaveDelay"/> if anything differs.</summary>
    public void UpdateTabs(Guid workspaceId, IReadOnlyList<TabState> tabs, int activeTabIndex)
    {
        lock (_lock)
        {
            if (Get(workspaceId) is not { } workspace
                || (workspace.ActiveTabIndex == activeTabIndex && SameTabs(workspace.Tabs, tabs)))
            {
                return;
            }

            Put(workspace with { Tabs = tabs, ActiveTabIndex = activeTabIndex });
            ScheduleSave();
        }
    }

    /// <summary>
    /// Data access on the workspace's own session, opened on first use. A failed open is not cached, so the next
    /// call tries again. Cancelling <paramref name="cancellationToken"/> only stops waiting; the open continues.
    /// </summary>
    public async Task<IDataAccess> GetDataAsync(Guid workspaceId, CancellationToken cancellationToken)
    {
        Task<IDatabaseConnection> session;
        lock (_lock)
        {
            if (Get(workspaceId) is not { IsOpen: true } workspace || _profile is not { } profile)
            {
                throw new InvalidOperationException("Der Workspace ist nicht geöffnet.");
            }

            if (!_sessions.TryGetValue(workspaceId, out session!) || session.IsFaulted || session.IsCanceled)
            {
                var token = _lifetime.Token;
                session = Task.Run(() => OpenSessionAsync(profile, workspace.Name, token), CancellationToken.None);
                _sessions[workspaceId] = session;
            }
        }

        var connection = await session.WaitAsync(cancellationToken);
        return connection.Data;
    }

    /// <summary>Writes all pending changes now.</summary>
    public async Task FlushAsync()
    {
        await _saveGate.WaitAsync();
        try
        {
            List<Workspace> pending;
            lock (_lock)
            {
                _saveTimer?.Dispose();
                _saveTimer = null;
                pending = _workspaces.Where(w => _dirty.Contains(w.Id)).ToList();
                _dirty.Clear();
            }

            string? error = null;
            foreach (var workspace in pending)
            {
                try
                {
                    await store.SaveAsync(workspace, CancellationToken.None);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    lock (_lock)
                    {
                        _dirty.Add(workspace.Id);
                    }

                    error = $"Workspace „{workspace.Name}“ konnte nicht gespeichert werden: {ex.Message}";
                }
            }

            if (SaveError != error)
            {
                SaveError = error;
                RaiseChanged();
            }
        }
        finally
        {
            _saveGate.Release();
        }
    }

    public ValueTask DisposeAsync() => new(DetachAsync());

    private async Task<IDatabaseConnection> OpenSessionAsync(ConnectionProfile profile, string action, CancellationToken cancellationToken)
    {
        var password = connections.GetPassword(profile.Id)
            ?? throw new DatabaseException("Für diese Verbindung ist kein Passwort gespeichert. Bitte unter „Bearbeiten“ eingeben.");
        return await connector.OpenAsync(profile, password, action, cancellationToken);
    }

    private static async Task SetActionAsync(Task<IDatabaseConnection> session, string action)
    {
        try
        {
            var connection = await session;
            await connection.SetActionAsync(action, CancellationToken.None);
        }
        catch (Exception ex) when (ex is DatabaseException or OperationCanceledException or ObjectDisposedException)
        {
            // The session failed to open or was closed meanwhile; a new one picks up the current name.
        }
    }

    private static async Task DisposeSessionAsync(Task<IDatabaseConnection> session)
    {
        try
        {
            var connection = await session;
            await connection.DisposeAsync();
        }
        catch (Exception ex) when (ex is DatabaseException or OperationCanceledException)
        {
            // Never opened: nothing to close.
        }
    }

    /// <summary>Records hold lists, so record equality would compare references; the JSON form is what gets saved anyway.</summary>
    private static bool SameTabs(IReadOnlyList<TabState> a, IReadOnlyList<TabState> b) =>
        JsonSerializer.Serialize(a, ConnectionStore.JsonOptions) == JsonSerializer.Serialize(b, ConnectionStore.JsonOptions);

    private Workspace NewWorkspace(string name) =>
        new(Guid.NewGuid(), _profile!.Id, name) { Order = NextOrder(), LastActive = _time.GetUtcNow() };

    /// <summary>"Workspace N" with the smallest N not in use.</summary>
    private string NextName()
    {
        var used = _workspaces.Select(w => w.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var n = 1;
        while (used.Contains(DefaultNamePrefix + n))
        {
            n++;
        }

        return DefaultNamePrefix + n;
    }

    private int NextOrder() => _workspaces.Where(w => w.IsOpen).Select(w => w.Order + 1).DefaultIfEmpty(0).Max();

    private List<Workspace> OpenOrdered() => _workspaces.Where(w => w.IsOpen).OrderBy(w => w.Order).ToList();

    private Workspace? Get(Guid workspaceId) => _workspaces.FirstOrDefault(w => w.Id == workspaceId);

    /// <summary>Adds or replaces the workspace and marks it for saving. Caller holds the lock.</summary>
    private void Put(Workspace workspace)
    {
        var index = _workspaces.FindIndex(w => w.Id == workspace.Id);
        if (index < 0)
        {
            _workspaces.Add(workspace);
        }
        else
        {
            _workspaces[index] = workspace;
        }

        _dirty.Add(workspace.Id);
    }

    private void ScheduleSave() =>
        _saveTimer ??= _time.CreateTimer(_ => _ = FlushAsync(), null, SaveDelay, Timeout.InfiniteTimeSpan);

    private void RequireAttached()
    {
        if (_profile is null)
        {
            throw new InvalidOperationException("Keine Verbindung aktiv.");
        }
    }

    private static ArgumentException InvalidName() =>
        new($"Der Name darf nicht leer und höchstens {Workspace.MaxNameLength} Zeichen lang sein.", "name");

    private void RaiseChanged() => Changed?.Invoke();
}
