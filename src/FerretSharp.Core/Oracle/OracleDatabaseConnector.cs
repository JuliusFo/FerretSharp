using FerretSharp.Core.Connections;
using FerretSharp.Core.Data;
using FerretSharp.Core.Schema;

namespace FerretSharp.Core.Oracle;

public sealed class OracleDatabaseConnector : IDatabaseConnector
{
    public async Task<IDatabaseConnection> OpenAsync(
        ConnectionProfile profile, string password, string action, CancellationToken cancellationToken)
    {
        try
        {
            var connectionString = OracleConnectionStringFactory.Create(profile, password);
            var session = await OracleSession.OpenAsync(
                connectionString, new SessionContext(OracleSessionDefaults.Module, action), cancellationToken);
            return new OracleDatabaseConnection(session);
        }
        catch (Exception ex) when (OracleErrors.Translate(ex) is { } translated)
        {
            throw translated;
        }
    }

    /// <summary>
    /// The session behind the interfaces the layers above see. No translating wrappers: the session's methods already
    /// report Oracle errors as <see cref="DatabaseException"/> (one place, <c>OracleSession.ExclusiveAsync</c>).
    /// </summary>
    private sealed class OracleDatabaseConnection(OracleSession session) : IDatabaseConnection
    {
        public string ServerVersion => session.ServerVersion;

        public ISchemaReader Schema { get; } = new OracleSchemaReader(session);

        public IDataAccess Data { get; } = new OracleDataAccess(session);

        public IDataEditor Editor { get; } = new OracleDataEditor(session);

        public TransactionInfo Transaction => session.Transaction;

        public bool UsesReadOnlySnapshots => session.UsesReadOnlySnapshots;

        public Task SetActionAsync(string action, CancellationToken cancellationToken) => session.SetActionAsync(action, cancellationToken);

        public Task<bool> PingIfIdleAsync(TimeSpan idleFor, CancellationToken cancellationToken) => session.PingIfIdleAsync(idleFor, cancellationToken);

        public Task UseReadOnlySnapshotsAsync(CancellationToken cancellationToken) => session.UseReadOnlySnapshotsAsync(cancellationToken);

        public Task StopReadOnlySnapshotsAsync(CancellationToken cancellationToken) => session.StopReadOnlySnapshotsAsync(cancellationToken);

        public ValueTask DisposeAsync() => session.DisposeAsync();
    }
}
