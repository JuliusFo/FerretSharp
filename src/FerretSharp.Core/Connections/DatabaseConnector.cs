using FerretSharp.Core.Data;
using FerretSharp.Core.Schema;

namespace FerretSharp.Core.Connections;

/// <summary>An open database session used for browsing (one per workspace from WP-05 on).</summary>
public interface IDatabaseConnection : IAsyncDisposable
{
    string ServerVersion { get; }

    ISchemaReader Schema { get; }

    IDataAccess Data { get; }
}

public interface IDatabaseConnector
{
    /// <param name="action">Shown as ACTION in <c>V$SESSION</c>, e.g. the workspace name.</param>
    Task<IDatabaseConnection> OpenAsync(ConnectionProfile profile, string password, string action, CancellationToken cancellationToken);
}

/// <summary>Failure with a user-facing message and, if available, the Oracle error code (<c>ORA-01017</c>).</summary>
public sealed class DatabaseException(string message, string? errorCode = null, Exception? inner = null) : Exception(message, inner)
{
    public string? ErrorCode { get; } = errorCode;
}
