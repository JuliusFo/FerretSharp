using FerretSharp.Core.Connections;
using FerretSharp.Core.Data;
using FerretSharp.Core.Query;
using FerretSharp.Core.Schema;
using Oracle.ManagedDataAccess.Client;

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

    private sealed class OracleDatabaseConnection(OracleSession session) : IDatabaseConnection
    {
        public string ServerVersion => session.ServerVersion;

        public ISchemaReader Schema { get; } = new TranslatingSchemaReader(new OracleSchemaReader(session));

        public IDataAccess Data { get; } = new TranslatingDataAccess(new OracleDataAccess(session));

        public Task SetActionAsync(string action, CancellationToken cancellationToken) => session.SetActionAsync(action, cancellationToken);

        public Task<bool> PingIfIdleAsync(TimeSpan idleFor, CancellationToken cancellationToken) =>
            OracleErrors.Guard(() => session.PingIfIdleAsync(idleFor, cancellationToken));

        public TransactionInfo Transaction => session.Transaction;

        public IDataEditor Editor { get; } = new OracleDataEditor(session);

        public Task UseReadOnlySnapshotsAsync(CancellationToken cancellationToken) =>
            OracleErrors.Guard(async () =>
            {
                await session.UseReadOnlySnapshotsAsync(cancellationToken);
                return true;
            });

        public Task StopReadOnlySnapshotsAsync(CancellationToken cancellationToken) =>
            OracleErrors.Guard(async () =>
            {
                await session.StopReadOnlySnapshotsAsync(cancellationToken);
                return true;
            });

        public bool UsesReadOnlySnapshots => session.UsesReadOnlySnapshots;

        public ValueTask DisposeAsync() => session.DisposeAsync();
    }

    private sealed class TranslatingDataAccess(IDataAccess inner) : IDataAccess
    {
        public Task<RowPage> ReadPageAsync(
            TableDetails table, IReadOnlyList<FilterCondition> filters, IReadOnlyList<SortSpec> sorts, PageSpec page, CancellationToken cancellationToken) =>
            OracleErrors.Guard(() => inner.ReadPageAsync(table, filters, sorts, page, cancellationToken));

        public Task<long> CountAsync(TableDetails table, IReadOnlyList<FilterCondition> filters, CancellationToken cancellationToken) =>
            OracleErrors.Guard(() => inner.CountAsync(table, filters, cancellationToken));

        public Task<LobRead> ReadLobAsync(TableDetails table, RowKey key, int column, CancellationToken cancellationToken) =>
            OracleErrors.Guard(() => inner.ReadLobAsync(table, key, column, cancellationToken));
    }

    /// <summary>Keeps OracleException out of the layers above.</summary>
    private sealed class TranslatingSchemaReader(ISchemaReader inner) : ISchemaReader
    {
        public Task<IReadOnlyList<TableSummary>> GetTablesAsync(string owner, CancellationToken cancellationToken) =>
            OracleErrors.Guard(() => inner.GetTablesAsync(owner, cancellationToken));

        public Task<IReadOnlyList<TableSummary>> GetSynonymTargetsAsync(string owner, CancellationToken cancellationToken) =>
            OracleErrors.Guard(() => inner.GetSynonymTargetsAsync(owner, cancellationToken));

        public Task<IReadOnlyList<ForeignKeyInfo>> GetForeignKeysAsync(string owner, CancellationToken cancellationToken) =>
            OracleErrors.Guard(() => inner.GetForeignKeysAsync(owner, cancellationToken));

        public Task<TableDetails> GetDetailsAsync(TableSummary table, CancellationToken cancellationToken) =>
            OracleErrors.Guard(() => inner.GetDetailsAsync(table, cancellationToken));

        public Task<ObjectInfo> GetObjectInfoAsync(TableSummary table, CancellationToken cancellationToken) =>
            OracleErrors.Guard(() => inner.GetObjectInfoAsync(table, cancellationToken));

        public Task<IReadOnlyList<ConstraintInfo>> GetConstraintsAsync(TableRef table, CancellationToken cancellationToken) =>
            OracleErrors.Guard(() => inner.GetConstraintsAsync(table, cancellationToken));

        public Task<IReadOnlyList<IndexInfo>> GetIndexesAsync(TableRef table, CancellationToken cancellationToken) =>
            OracleErrors.Guard(() => inner.GetIndexesAsync(table, cancellationToken));

        public Task<ObjectDependencies> GetDependenciesAsync(TableSummary table, CancellationToken cancellationToken) =>
            OracleErrors.Guard(() => inner.GetDependenciesAsync(table, cancellationToken));

        public Task<string> GetDdlAsync(TableSummary table, CancellationToken cancellationToken) =>
            OracleErrors.Guard(() => inner.GetDdlAsync(table, cancellationToken));

        public Task<IReadOnlyList<LockHolder>?> GetLockHoldersAsync(TableRef table, CancellationToken cancellationToken) =>
            OracleErrors.Guard(() => inner.GetLockHoldersAsync(table, cancellationToken));
    }
}

internal static class OracleErrors
{
    /// <summary>Maps driver/configuration failures to <see cref="DatabaseException"/>; null for anything else.</summary>
    public static DatabaseException? Translate(Exception ex) => ex switch
    {
        OracleStatementException { Oracle: { } oracle } statement => new DatabaseException(
            OracleConnectionTester.CleanMessage(oracle.Message), $"ORA-{oracle.Number:00000}", oracle) { Statement = statement.Statement },
        OracleStatementException closed => new DatabaseException(closed.Message, inner: closed)
        {
            Statement = closed.Statement,
            IsConnectionLost = true,
        },
        OracleException oracle => new DatabaseException(
            OracleConnectionTester.CleanMessage(oracle.Message), $"ORA-{oracle.Number:00000}", oracle),
        ConnectionConfigurationException config => new DatabaseException(config.Message, inner: config),
        _ => null,
    };

    public static async Task<T> Guard<T>(Func<Task<T>> action)
    {
        try
        {
            return await action();
        }
        catch (Exception ex) when (Translate(ex) is { } translated)
        {
            throw translated;
        }
    }
}
