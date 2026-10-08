using FerretSharp.Core.IO;
using System.Text.Json;
using FerretSharp.Core.Connections;
using FerretSharp.Core.Data;

namespace FerretSharp.Core.Workspaces;

/// <summary>
/// Workspaces of the connected profile: which are open, which is active, and one database session per open
/// workspace (ACTION = workspace name), opened on first use (<see cref="WorkspaceSessions"/>). Changes are saved shortly
/// after they happen (<see cref="SaveDelay"/>) and immediately on structural changes (create, close, reopen, detach)
/// (<see cref="WorkspaceSaver"/>).
/// </summary>
public sealed class WorkspaceManager : IAsyncDisposable
{
    public static readonly TimeSpan SaveDelay = TimeSpan.FromSeconds(1);

    private const string DefaultNamePrefix = "Workspace ";

    private readonly IWorkspaceStore _store;
    private readonly TimeProvider _time;
    private readonly WorkspaceSessions _sessions;
    private readonly WorkspaceSaver _saver;

    /// <summary>Guards the registry; taken before the sessions' own lock, never the other way round.</summary>
    private readonly Lock _lock = new();
    private List<Workspace> _workspaces = [];
    private Guid? _activeId;
    private ConnectionProfile? _profile;

    public WorkspaceManager(IWorkspaceStore store, ConnectionManager connections, IDatabaseConnector connector, TimeProvider? timeProvider = null)
    {
        _store = store;
        _time = timeProvider ?? TimeProvider.System;
        _sessions = new WorkspaceSessions(connections, connector);
        _saver = new WorkspaceSaver(store, _time, Current, RaiseChanged);
    }

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
    public string? SaveError => _saver.Error;

    /// <summary>Transaction of the workspace's session; null while the session is not open (yet).</summary>
    public TransactionInfo? TransactionOf(Guid workspaceId) => _sessions.Connection(workspaceId)?.Transaction;

    /// <summary>The uncommitted writes of the workspace's transaction (status bar, undo); empty while the session is not open.</summary>
    public IReadOnlyList<WriteAction> ActionsOf(Guid workspaceId) => _sessions.Connection(workspaceId)?.Editor.Actions ?? [];

    /// <summary>
    /// Whether the workspace may write: always on profiles without the read-only lock, on locked profiles (Prod by
    /// default) only after <see cref="UnlockAsync"/>.
    /// </summary>
    public bool IsWritable(Guid workspaceId) => _sessions.IsWritable(workspaceId);

    /// <summary>A workspace of a read-only profile that the user unlocked for writing (shown prominently).</summary>
    public bool IsUnlocked(Guid workspaceId) => _sessions.IsUnlocked(workspaceId);

    /// <summary>
    /// Unlocks one workspace of a read-only profile for writing (WP-10): its session leaves the read-only transaction.
    /// Per workspace and never saved – closing the workspace, disconnecting or reconnecting locks it again.
    /// </summary>
    public async Task UnlockAsync(Guid workspaceId, CancellationToken cancellationToken)
    {
        if (!_sessions.MarkUnlocked(workspaceId))
        {
            return;
        }

        try
        {
            // A session opened from now on stays unlocked; one already open (or opening) is unlocked here.
            var connection = await GetConnectionAsync(workspaceId, cancellationToken);
            await connection.StopReadOnlySnapshotsAsync(cancellationToken);
        }
        catch
        {
            _sessions.MarkLocked(workspaceId);
            throw;
        }

        RaiseChanged();
    }

    /// <summary>
    /// Locks an unlocked workspace again: its session goes back into a read-only transaction. A writing transaction
    /// must be committed or rolled back first.
    /// </summary>
    /// <exception cref="RefusedException">A writing transaction is still open.</exception>
    public async Task LockAsync(Guid workspaceId, CancellationToken cancellationToken)
    {
        try
        {
            await _sessions.LockAsync(workspaceId, cancellationToken);
        }
        finally
        {
            RaiseChanged();
        }
    }

    /// <summary>Workspace sessions that are open (for the keep-alive); sessions still opening or failed are left out.</summary>
    public IReadOnlyList<IDatabaseConnection> OpenSessions() => _sessions.Open();

    public Workspace? Find(Guid workspaceId)
    {
        lock (_lock)
        {
            return Get(workspaceId);
        }
    }

    /// <summary>
    /// Loads the workspaces of <paramref name="profile"/>. Makes sure one is open: reopens the most recently used one,
    /// or creates "Workspace 1". The most recently active open workspace becomes active.
    /// </summary>
    public async Task AttachAsync(ConnectionProfile profile, CancellationToken cancellationToken)
    {
        await DetachAsync();
        var result = await _store.LoadAsync(profile.Id, cancellationToken);
        lock (_lock)
        {
            _profile = profile;
            _sessions.Attach(profile);
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
        IReadOnlyList<Task<IDatabaseConnection>> sessions;
        lock (_lock)
        {
            if (_profile is null)
            {
                return;
            }

            sessions = _sessions.Detach();
        }

        await FlushAsync();
        foreach (var session in sessions)
        {
            await WorkspaceSessions.CloseAsync(session);
        }

        // Rolling back can take a while; tab changes recorded meanwhile are saved too.
        await FlushAsync();
        lock (_lock)
        {
            _saver.Reset();
            _profile = null;
            _workspaces = [];
            _activeId = null;
            LoadErrors = [];
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
            _saver.Schedule();
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
            _saver.Schedule();
            session = _sessions.Find(workspaceId);
        }

        if (session is not null)
        {
            _ = WorkspaceSessions.SetActionAsync(session, normalized);
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
                throw new RefusedException("Der letzte offene Workspace kann nicht geschlossen werden.");
            }

            Put(open[index] with { IsOpen = false });
            if (_activeId == workspaceId)
            {
                var next = open[index + 1 < open.Count ? index + 1 : index - 1];
                Put(next with { LastActive = _time.GetUtcNow() });
                _activeId = next.Id;
            }

            session = _sessions.Remove(workspaceId);
        }

        await FlushAsync();
        RaiseChanged();
        if (session is not null)
        {
            await WorkspaceSessions.CloseAsync(session);
        }
    }

    /// <summary>
    /// Gives up the workspace's session, e.g. when a statement does not react to cancelling: closes its connection (Oracle
    /// rolls back an open transaction; a statement still running there ends as cancelled). The workspace stays open and
    /// unlocked; its next database access opens a new session.
    /// </summary>
    public async Task ResetSessionAsync(Guid workspaceId)
    {
        Task<IDatabaseConnection>? session;
        lock (_lock)
        {
            session = _sessions.Drop(workspaceId);
        }

        if (session is null)
        {
            return;
        }

        RaiseChanged();
        await WorkspaceSessions.CloseAsync(session);
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
                throw new RefusedException("Nur geschlossene Workspaces können gelöscht werden.");
            }

            _workspaces.Remove(workspace);
            _saver.Forget(workspaceId);
        }

        await _store.DeleteAsync(workspaceId, CancellationToken.None);
        RaiseChanged();
    }

    /// <summary>Removes the stored workspaces of a deleted connection.</summary>
    public Task DeleteForConnectionAsync(Guid connectionId) => _store.DeleteForConnectionAsync(connectionId, CancellationToken.None);

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
            _saver.Schedule();
        }
    }

    /// <summary>
    /// Data access on the workspace's own session, opened on first use. A failed open is not cached, so the next
    /// call tries again. Cancelling <paramref name="cancellationToken"/> only stops waiting; the open continues.
    /// </summary>
    public async Task<IDataAccess> GetDataAsync(Guid workspaceId, CancellationToken cancellationToken) =>
        (await GetConnectionAsync(workspaceId, cancellationToken)).Data;

    /// <summary>Writing on the workspace's session (same transaction as its queries, so they see the flushed changes).</summary>
    public async Task<IDataEditor> GetEditorAsync(Guid workspaceId, CancellationToken cancellationToken) =>
        (await GetConnectionAsync(workspaceId, cancellationToken)).Editor;

    /// <summary>Writes all pending changes now.</summary>
    public Task FlushAsync() => _saver.FlushAsync();

    public ValueTask DisposeAsync() => new(DetachAsync());

    private async Task<IDatabaseConnection> GetConnectionAsync(Guid workspaceId, CancellationToken cancellationToken)
    {
        Task<IDatabaseConnection> session;
        lock (_lock)
        {
            // Under the registry's lock: a workspace being closed right now cannot get a new session.
            if (Get(workspaceId) is not { IsOpen: true } workspace || _profile is null)
            {
                throw new WorkspaceClosedException();
            }

            session = _sessions.GetOrOpen(workspaceId, workspace.Name);
        }

        return await session.WaitAsync(cancellationToken);
    }

    /// <summary>The current state of the given workspaces, for the saver.</summary>
    private IReadOnlyList<Workspace> Current(IReadOnlyCollection<Guid> workspaceIds)
    {
        lock (_lock)
        {
            return _workspaces.Where(w => workspaceIds.Contains(w.Id)).ToList();
        }
    }

    /// <summary>Records hold lists, so record equality would compare references; the JSON form is what gets saved anyway.</summary>
    private static bool SameTabs(IReadOnlyList<TabState> a, IReadOnlyList<TabState> b) =>
        JsonSerializer.Serialize(a, JsonFiles.Options) == JsonSerializer.Serialize(b, JsonFiles.Options);

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

        _saver.MarkDirty(workspace.Id);
    }

    private void RequireAttached()
    {
        if (_profile is null)
        {
            throw new RefusedException("Keine Verbindung aktiv.");
        }
    }

    private static ArgumentException InvalidName() =>
        new($"Der Name darf nicht leer und höchstens {Workspace.MaxNameLength} Zeichen lang sein.", "name");

    private void RaiseChanged() => Changed?.Invoke();
}
