using FerretSharp.Core.Data;
using FerretSharp.Core.Query;
using FerretSharp.Core.Schema;

namespace FerretSharp.Core.Connections;

/// <summary>An open database session used for browsing (one per workspace from WP-05 on).</summary>
public interface IDatabaseConnection : IAsyncDisposable
{
    string ServerVersion { get; }

    ISchemaReader Schema { get; }

    IDataAccess Data { get; }

    /// <summary>Updates ACTION in <c>V$SESSION</c>, e.g. after the workspace was renamed.</summary>
    Task SetActionAsync(string action, CancellationToken cancellationToken);

    /// <summary>
    /// Keep-alive round trip if the session has been idle for at least <paramref name="idleFor"/>; a busy or closed
    /// session is skipped. False if it did not ping.
    /// </summary>
    /// <exception cref="DatabaseException">The ping failed, e.g. with <see cref="DatabaseException.IsConnectionLost"/>.</exception>
    Task<bool> PingIfIdleAsync(TimeSpan idleFor, CancellationToken cancellationToken);

    TransactionInfo Transaction { get; }

    /// <summary>Writing in this session's transaction (v2); refused by Oracle on a locked session.</summary>
    IDataEditor Editor { get; }

    /// <summary>
    /// Locks the session for a read-only profile: it runs in <c>SET TRANSACTION READ ONLY</c> from now on, so Oracle
    /// rejects DML. Every new query (first page) starts a new snapshot.
    /// </summary>
    Task UseReadOnlySnapshotsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Unlocks the session (WP-10): ends the read-only transaction, writing becomes possible. Locking again needs the
    /// writing transaction to be committed or rolled back first.
    /// </summary>
    Task StopReadOnlySnapshotsAsync(CancellationToken cancellationToken);

    /// <summary>Locked by <see cref="UseReadOnlySnapshotsAsync"/>: Oracle rejects any DML.</summary>
    bool UsesReadOnlySnapshots { get; }
}

public interface IDatabaseConnector
{
    /// <param name="action">Shown as ACTION in <c>V$SESSION</c>, e.g. the workspace name.</param>
    Task<IDatabaseConnection> OpenAsync(ConnectionProfile profile, string password, string action, CancellationToken cancellationToken);
}
