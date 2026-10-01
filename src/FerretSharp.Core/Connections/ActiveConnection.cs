using FerretSharp.Core.Schema;
using FerretSharp.Core.Workspaces;

namespace FerretSharp.Core.Connections;

public enum ConnectionStatus
{
    Disconnected,
    Connecting,
    Connected,
    Failed,
}

/// <param name="ErrorCode">e.g. <c>ORA-01017</c>; null for non-Oracle failures.</param>
public sealed record ConnectionError(string Message, string? ErrorCode);

/// <summary>
/// The connection currently being browsed: opens the explorer session, loads the schema cache, attaches the
/// workspaces (each with its own data session, see <see cref="WorkspaceManager"/>) and records usage.
/// </summary>
public sealed class ActiveConnection(
    ConnectionManager connections, IDatabaseConnector connector, RecentConnections recent, WorkspaceManager workspaces) : IAsyncDisposable
{
    public const string ExplorerAction = "Explorer";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource? _connectCts;
    private IDatabaseConnection? _connection;

    /// <summary>Raised on every status change; may fire on a background thread.</summary>
    public event Action? Changed;

    public ConnectionStatus Status { get; private set; }

    public ConnectionProfile? Profile { get; private set; }

    public string? ServerVersion { get; private set; }

    public SchemaCache? Schema { get; private set; }

    public ConnectionError? Error { get; private set; }

    public bool IsConnected => Status == ConnectionStatus.Connected;

    /// <summary>Closes any current connection, then opens <paramref name="profile"/>. Failures end in <see cref="ConnectionStatus.Failed"/>.</summary>
    public async Task ConnectAsync(ConnectionProfile profile, CancellationToken cancellationToken)
    {
        CancelPendingConnect();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _connectCts = cts;

        await _gate.WaitAsync(CancellationToken.None);
        try
        {
            await CloseAsync();
            Set(ConnectionStatus.Connecting, profile);

            var password = connections.GetPassword(profile.Id)
                ?? throw new DatabaseException("Für diese Verbindung ist kein Passwort gespeichert. Bitte unter „Bearbeiten“ eingeben.");

            _connection = await connector.OpenAsync(profile, password, ExplorerAction, cts.Token);
            var schema = new SchemaCache(_connection.Schema, profile.EffectiveSchema);
            await schema.LoadAsync(cts.Token);
            await workspaces.AttachAsync(profile, cts.Token);

            Schema = schema;
            ServerVersion = _connection.ServerVersion;
            Set(ConnectionStatus.Connected, profile);
        }
        catch (OperationCanceledException)
        {
            await CloseAsync();
            Set(ConnectionStatus.Disconnected, null);
            return;
        }
        catch (DatabaseException ex)
        {
            await CloseAsync();
            Set(ConnectionStatus.Failed, profile, new ConnectionError(ex.Message, ex.ErrorCode));
            return;
        }
        catch (Exception ex)
        {
            // Never leave the UI stuck in "Connecting".
            await CloseAsync();
            Set(ConnectionStatus.Failed, profile, new ConnectionError(ex.Message, null));
            return;
        }
        finally
        {
            if (ReferenceEquals(_connectCts, cts))
            {
                _connectCts = null;
            }

            _gate.Release();
        }

        try
        {
            await recent.MarkUsedAsync(profile.Id, CancellationToken.None);
        }
        catch (IOException)
        {
            // History is a convenience; a locked or read-only file must not affect the connection.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public void CancelPendingConnect() => _connectCts?.Cancel();

    public async Task DisconnectAsync()
    {
        CancelPendingConnect();
        await _gate.WaitAsync();
        try
        {
            await CloseAsync();
            Set(ConnectionStatus.Disconnected, null);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        CancelPendingConnect();
        await CloseAsync();
    }

    private async Task CloseAsync()
    {
        await workspaces.DetachAsync();
        Schema = null;
        ServerVersion = null;
        if (_connection is not null)
        {
            await _connection.DisposeAsync();
            _connection = null;
        }
    }

    private void Set(ConnectionStatus status, ConnectionProfile? profile, ConnectionError? error = null)
    {
        Status = status;
        Profile = profile;
        Error = error;
        Changed?.Invoke();
    }
}
