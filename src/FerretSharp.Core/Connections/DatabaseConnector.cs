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
}

public interface IDatabaseConnector
{
    /// <param name="action">Shown as ACTION in <c>V$SESSION</c>, e.g. the workspace name.</param>
    Task<IDatabaseConnection> OpenAsync(ConnectionProfile profile, string password, string action, CancellationToken cancellationToken);
}

/// <summary>Failure with a user-facing message and, if available, the Oracle error code (<c>ORA-01017</c>).</summary>
public sealed class DatabaseException(string message, string? errorCode = null, Exception? inner = null) : Exception(message, inner)
{
    /// <summary>
    /// Errors after which the session is gone: killed, idle timeout (profile <c>IDLE_TIME</c>), network or instance
    /// failure. Only a new connection helps.
    /// </summary>
    private static readonly HashSet<string> ConnectionLostCodes =
    [
        "ORA-00028", "ORA-00603", "ORA-01012", "ORA-01089", "ORA-01092", "ORA-02396", "ORA-03113", "ORA-03114",
        "ORA-03135", "ORA-12537", "ORA-12547", "ORA-12570", "ORA-12571",
    ];

    public string? ErrorCode { get; } = errorCode;

    /// <summary>The statement that failed, if the error came from one (shown in the error dialog, binds masked on Prod).</summary>
    public QuerySpec? Statement { get; init; }

    /// <summary>The session is gone; reconnecting is the only remedy.</summary>
    public bool IsConnectionLost { get; init; } = IsConnectionLostCode(errorCode);

    public static bool IsConnectionLostCode(string? errorCode) => errorCode is not null && ConnectionLostCodes.Contains(errorCode);

    /// <summary>"ORA-00942: Tabelle … ist nicht vorhanden" or just the message.</summary>
    public string Display => ErrorCode is null ? Message : $"{ErrorCode}: {Message}";
}
