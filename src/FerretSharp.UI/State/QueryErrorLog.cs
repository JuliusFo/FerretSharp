using FerretSharp.Core.Connections;
using FerretSharp.Core.Query;
using Microsoft.Extensions.Logging;

namespace FerretSharp.UI.State;

public static partial class QueryErrorLog
{
    /// <summary>Logs a failed query with its statement; bind values of Prod connections are masked.</summary>
    public static void Log(ILogger logger, DatabaseException error, ConnectionProfile? profile)
    {
        var statement = error.Statement is { } s ? BindValues.Describe(s, profile?.Kind == ConnectionKind.Prod) : "(kein Statement)";
        Failed(logger, error.Display, error.IsConnectionLost, statement);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Query failed: {Error} (connection lost: {Lost})\n{Statement}")]
    private static partial void Failed(ILogger logger, string error, bool lost, string statement);
}
