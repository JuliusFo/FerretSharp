using FerretSharp.Core.Connections;
using FerretSharp.Core.Data;

namespace FerretSharp.Core.Workspaces;

/// <summary>
/// The database sessions of the open workspaces (R2, out of <see cref="WorkspaceManager"/>): one per workspace, opened
/// on first use, locked in a read-only transaction on read-only profiles unless the workspace was unlocked (WP-10).
/// Thread-safe; the manager calls the synchronous members while it holds its own lock (always in that order), so opening
/// a session and closing its workspace cannot cross.
/// </summary>
internal sealed class WorkspaceSessions(ConnectionManager connections, IDatabaseConnector connector)
{
    private readonly Lock _lock = new();
    private readonly Dictionary<Guid, Task<IDatabaseConnection>> _sessions = [];
    private readonly HashSet<Guid> _unlocked = [];
    private ConnectionProfile? _profile;
    private CancellationTokenSource _lifetime = new();

    public void Attach(ConnectionProfile profile)
    {
        lock (_lock)
        {
            _profile = profile;
            _lifetime = new CancellationTokenSource();
        }
    }

    /// <summary>Forgets every session and unlock; the sessions are returned to be closed by the caller.</summary>
    public IReadOnlyList<Task<IDatabaseConnection>> Detach()
    {
        lock (_lock)
        {
            _lifetime.Cancel(); // not disposed: sessions still opening hold its token
            var sessions = _sessions.Values.ToList();
            _sessions.Clear();
            _unlocked.Clear();
            _profile = null;
            return sessions;
        }
    }

    /// <summary>The workspace's session; opened (in the background) if there is none or the last attempt failed.</summary>
    public Task<IDatabaseConnection> GetOrOpen(Guid workspaceId, string action)
    {
        lock (_lock)
        {
            var profile = _profile ?? throw new WorkspaceClosedException();
            if (!_sessions.TryGetValue(workspaceId, out var session) || session.IsFaulted || session.IsCanceled)
            {
                var token = _lifetime.Token;
                session = Task.Run(() => OpenAsync(profile, workspaceId, action, token), CancellationToken.None);
                _sessions[workspaceId] = session;
            }

            return session;
        }
    }

    /// <summary>The session, if one was opened or is opening.</summary>
    public Task<IDatabaseConnection>? Find(Guid workspaceId)
    {
        lock (_lock)
        {
            return _sessions.GetValueOrDefault(workspaceId);
        }
    }

    /// <summary>The workspace is closed: its session leaves the pool (closed by the caller), the unlock ends.</summary>
    public Task<IDatabaseConnection>? Remove(Guid workspaceId)
    {
        lock (_lock)
        {
            _unlocked.Remove(workspaceId); // reopened later, it starts locked again
            _sessions.Remove(workspaceId, out var session);
            return session;
        }
    }

    /// <summary>Sessions that are open (for the keep-alive); those still opening or failed are left out.</summary>
    public IReadOnlyList<IDatabaseConnection> Open()
    {
        lock (_lock)
        {
            return _sessions.Values.Where(s => s.IsCompletedSuccessfully).Select(s => s.Result).ToList();
        }
    }

    /// <summary>The open session's connection; null while it is not open (yet).</summary>
    public IDatabaseConnection? Connection(Guid workspaceId)
    {
        lock (_lock)
        {
            return _sessions.GetValueOrDefault(workspaceId) is { IsCompletedSuccessfully: true } session ? session.Result : null;
        }
    }

    public bool IsWritable(Guid workspaceId)
    {
        lock (_lock)
        {
            return _profile is { } profile && (!profile.ReadOnly || _unlocked.Contains(workspaceId));
        }
    }

    public bool IsUnlocked(Guid workspaceId)
    {
        lock (_lock)
        {
            return _profile is { ReadOnly: true } && _unlocked.Contains(workspaceId);
        }
    }

    /// <summary>Marks the workspace unlocked; false if the profile is not read-only or it already was.</summary>
    public bool MarkUnlocked(Guid workspaceId)
    {
        lock (_lock)
        {
            return _profile is { ReadOnly: true } && _unlocked.Add(workspaceId);
        }
    }

    public void MarkLocked(Guid workspaceId)
    {
        lock (_lock)
        {
            _unlocked.Remove(workspaceId);
        }
    }

    /// <summary>
    /// Locks an unlocked workspace again: its session goes back into a read-only transaction. A writing transaction must
    /// be committed or rolled back first. A session that cannot be locked again is dropped – the next one starts locked.
    /// </summary>
    public async Task LockAsync(Guid workspaceId, CancellationToken cancellationToken)
    {
        Task<IDatabaseConnection>? session;
        lock (_lock)
        {
            if (!_unlocked.Contains(workspaceId))
            {
                return;
            }

            session = _sessions.GetValueOrDefault(workspaceId);
            if (session is { IsCompletedSuccessfully: true } && session.Result.Transaction.Mode == TransactionMode.ReadWrite)
            {
                throw new RefusedException("Erst committen oder verwerfen, dann sperren.");
            }

            _unlocked.Remove(workspaceId);
        }

        if (session is null)
        {
            return;
        }

        try
        {
            var connection = await session;
            await connection.UseReadOnlySnapshotsAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is DatabaseException or OperationCanceledException)
        {
            lock (_lock)
            {
                if (_sessions.GetValueOrDefault(workspaceId) == session)
                {
                    _sessions.Remove(workspaceId);
                }
            }

            _ = CloseAsync(session);
            throw;
        }
    }

    /// <summary>Sets ACTION of the session (after renaming the workspace); nothing if it never opened or was closed.</summary>
    public static async Task SetActionAsync(Task<IDatabaseConnection> session, string action)
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

    public static async Task CloseAsync(Task<IDatabaseConnection> session)
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

    private async Task<IDatabaseConnection> OpenAsync(ConnectionProfile profile, Guid workspaceId, string action, CancellationToken cancellationToken)
    {
        var password = connections.GetPassword(profile.Id)
            ?? throw new DatabaseException("Für diese Verbindung ist kein Passwort gespeichert. Bitte unter „Bearbeiten“ eingeben.");
        var connection = await connector.OpenAsync(profile, password, action, cancellationToken);
        if (!profile.ReadOnly || IsUnlocked(workspaceId))
        {
            return connection;
        }

        // Read-only profile (Prod by default): Oracle itself rejects DML in this session.
        try
        {
            await connection.UseReadOnlySnapshotsAsync(cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }
}
