using FerretSharp.Core.Query;
using FerretSharp.Core.Resources;

namespace FerretSharp.Core.Connections;

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

/// <summary>
/// FerretSharp refuses an operation for a reason the user should read (localized message): a locked workspace, an open
/// transaction, a row without key … Neither a database error nor a bug – the UI shows the message as it is. Derives from
/// <see cref="InvalidOperationException"/>, which these refusals were before (existing handlers keep working); plain
/// <see cref="InvalidOperationException"/>s stay for guards against programming mistakes.
/// </summary>
public class RefusedException(string message, Exception? inner = null) : InvalidOperationException(message, inner);

/// <summary>The workspace is not open (any more): closed or disconnected while a call for it was on its way.</summary>
public sealed class WorkspaceClosedException() : RefusedException(WorkspaceText.WorkspaceNotOpen);
