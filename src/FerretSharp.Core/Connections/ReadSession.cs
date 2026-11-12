using FerretSharp.Core.Data;
using FerretSharp.Core.Oracle;
using FerretSharp.Core.Resources;
using FerretSharp.Core.Workspaces;

namespace FerretSharp.Core.Connections;

/// <summary>
/// A session of the connection for reads of the committed state that must neither see a workspace's uncommitted work nor wait
/// behind its statements (WP-44, ADR 0047: the data diff; meant for the relationship check of WP-43 too). Only the read path
/// leaves it (<see cref="IDataAccess"/>). Opened on the first read through <see cref="SessionOpening"/>, closed after
/// <see cref="IdleTimeout"/> without a read and when the connection closes; pinged by the keep-alive like the others while
/// open. On a read-only profile it runs in read-only snapshots like a locked workspace. A session found lost is dropped, the
/// next read opens a new one – never by itself: a refused login counts towards <c>FAILED_LOGIN_ATTEMPTS</c>.
/// </summary>
/// <param name="time">The clock of the idle timeout and the limits on opening (tests); the system's if null.</param>
public sealed class ReadSession(ConnectionManager connections, IDatabaseConnector connector, TimeProvider? time = null)
{
    /// <summary>ACTION of the session in <c>V$SESSION</c>.</summary>
    public const string Action = "Reads";

    /// <summary>The session closes after this long without a read: a diff is read now and then, not all day.</summary>
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(5);

    private readonly Lock _lock = new();
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private ConnectionProfile? _profile;
    private CancellationTokenSource _lifetime = new();
    private Task<IDatabaseConnection>? _session;
    private int _reads;
    private ITimer? _idle;

    /// <summary>
    /// Why the database refused to log in (review of WP-44): no further attempt until the connection is opened again – a
    /// comparison of twelve tables tried twelve logins, and every refused one counts towards <c>FAILED_LOGIN_ATTEMPTS</c>.
    /// </summary>
    private string? _refused;

    /// <summary>
    /// <c>V$DATABASE</c> could not be read (WP-78): the current SCN is mapped from the time until the connection is opened again
    /// – one failing statement per connection, not one per start point.
    /// </summary>
    private bool _noExactScn;

    /// <summary>Whether a session is open or opening (tests, diagnostics).</summary>
    internal bool HasSession
    {
        get
        {
            lock (_lock)
            {
                return _session is { IsFaulted: false, IsCanceled: false };
            }
        }
    }

    /// <summary>The open session for the keep-alive; null while none is open.</summary>
    internal IDatabaseConnection? Open
    {
        get
        {
            lock (_lock)
            {
                return _session is { IsCompletedSuccessfully: true } session ? session.Result : null;
            }
        }
    }

    /// <summary>The connection is open: reads may open a session of <paramref name="profile"/>.</summary>
    internal void Attach(ConnectionProfile profile)
    {
        lock (_lock)
        {
            _profile = profile;
            _lifetime = new CancellationTokenSource();
            _refused = null;
            _noExactScn = false;
        }
    }

    /// <summary>
    /// The read path as it was at <paramref name="scn"/> (time travel, WP-78): every call a read of its own on this session, each
    /// table <c>AS OF SCN</c> – for a time travel tab what a workspace's data access is for the other tabs.
    /// </summary>
    public IDataAccess AsOf(long scn) => new ReadSessionData(this, scn);

    /// <summary>
    /// The database's current SCN, the start of a data diff (WP-44): exactly from <c>V$DATABASE</c> where the right is there,
    /// otherwise mapped from the time – a few seconds back (decision of the user, 2026-11-09). Without the right that is
    /// learned once per connection.
    /// </summary>
    public Task<DatabasePoint> CurrentPointAsync(CancellationToken cancellationToken) => ReadAsync(async (data, ct) =>
    {
        bool exact;
        lock (_lock)
        {
            exact = !_noExactScn;
        }

        if (exact)
        {
            try
            {
                return await data.CurrentPointAsync(exact: true, ct);
            }
            catch (DatabaseException ex) when (ex.IsAny(OracleErrorCodes.MissingRights))
            {
                lock (_lock)
                {
                    _noExactScn = true;
                }
            }
        }

        return await data.CurrentPointAsync(exact: false, ct);
    }, cancellationToken);

    /// <summary>The connection closes: the session too; reads still running end with a cancel.</summary>
    internal Task DetachAsync()
    {
        Task<IDatabaseConnection>? session;
        lock (_lock)
        {
            _lifetime.Cancel(); // not disposed: an open still running holds its token
            _profile = null;
            session = TakeSession();
        }

        return session is null ? Task.CompletedTask : CloseAsync(session);
    }

    /// <summary>
    /// Runs <paramref name="read"/> on the session's read path, opening the session first if there is none (or the last one
    /// failed to open). Runs off the calling thread: opening and reading block on ODP.NET's synchronous parts.
    /// </summary>
    /// <exception cref="RefusedException">The connection is not open, or the database refused to log in before.</exception>
    /// <exception cref="DatabaseException">Opening or reading failed; a lost session is dropped.</exception>
    public async Task<T> ReadAsync<T>(Func<IDataAccess, CancellationToken, Task<T>> read, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(read);
        Task<IDatabaseConnection> session;
        lock (_lock)
        {
            var profile = _profile ?? throw new RefusedException(ConnectionText.ReadSessionNotConnected);
            if (_refused is { } refused)
            {
                throw new RefusedException(TextFormat.Format(ConnectionText.ReadSessionRefused, refused));
            }

            if (_session is null or { IsFaulted: true } or { IsCanceled: true })
            {
                var token = _lifetime.Token;
                _session = Task.Run(() => OpenAsync(profile, token), CancellationToken.None);
            }

            session = _session;
            _reads++;
            _idle?.Dispose();
            _idle = null;
        }

        try
        {
            return await Task.Run(async () =>
            {
                var connection = await session.WaitAsync(cancellationToken);
                return await read(connection.Data, cancellationToken);
            }, CancellationToken.None);
        }
        catch (DatabaseException ex) when (ex.IsConnectionLost)
        {
            Drop(session);
            throw;
        }
        finally
        {
            lock (_lock)
            {
                _reads--;
                if (_reads == 0 && ReferenceEquals(_session, session))
                {
                    _idle = _time.CreateTimer(_ => CloseIfIdle(session), null, IdleTimeout, Timeout.InfiniteTimeSpan);
                }
            }
        }
    }

    private void CloseIfIdle(Task<IDatabaseConnection> session)
    {
        lock (_lock)
        {
            if (_reads > 0 || !ReferenceEquals(_session, session))
            {
                return;
            }

            TakeSession();
        }

        _ = CloseAsync(session);
    }

    private void Drop(Task<IDatabaseConnection> session)
    {
        lock (_lock)
        {
            if (!ReferenceEquals(_session, session))
            {
                return;
            }

            TakeSession();
        }

        _ = CloseAsync(session);
    }

    /// <summary>Removes the session (under the lock); the caller closes it.</summary>
    private Task<IDatabaseConnection>? TakeSession()
    {
        var session = _session;
        _session = null;
        _idle?.Dispose();
        _idle = null;
        return session;
    }

    /// <summary>Closes off the calling thread: ODP.NET closes synchronously and blocks on a silent line.</summary>
    private static Task CloseAsync(Task<IDatabaseConnection> session) =>
        Task.Run(() => WorkspaceSessions.CloseAsync(session), CancellationToken.None);

    private async Task<IDatabaseConnection> OpenAsync(ConnectionProfile profile, CancellationToken cancellationToken)
    {
        var password = connections.GetPassword(profile.Id)
            ?? throw new DatabaseException(ConnectionText.NoPasswordSaved);
        IDatabaseConnection connection;
        try
        {
            connection = await SessionOpening.OpenAsync(
                (_, token) => connector.OpenAsync(profile, password, Action, token), _time, retrying: null, cancellationToken);
        }
        catch (DatabaseException ex) when (ex is not SessionOpenTimeoutException && !ReconnectPolicy.IsNetworkError(ex.ErrorCode))
        {
            lock (_lock)
            {
                if (_profile == profile)
                {
                    _refused = ex.Display;
                }
            }

            if (ex.ErrorCode == ConnectionManager.WrongPasswordCode)
            {
                connections.ForgetSessionPassword(profile.Id); // asked again on the next connect, as ActiveConnection does
            }

            throw;
        }

        if (!profile.ReadOnly)
        {
            return connection;
        }

        // Read-only profile (Prod by default): Oracle itself rejects DML in this session, as in a locked workspace.
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
