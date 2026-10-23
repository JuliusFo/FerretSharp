using FerretSharp.Core.Connections;
using FerretSharp.Core.Query;
using Microsoft.Extensions.Logging;

namespace FerretSharp.UI.State;

public static partial class QueryErrorLog
{
    /// <summary>
    /// Logs a failed query with its statement; passwords in the text are masked, and on Prod connections the bind values and
    /// the string literals of the text too (review 3.40.0, SE-6). The error message itself stays: without it the entry says nothing.
    /// </summary>
    public static void Log(ILogger logger, DatabaseException error, ConnectionProfile? profile)
    {
        var masks = profile?.MasksValues == true;
        var statement = error.Statement is { } s ? SqlRedaction.Redact(BindValues.Describe(s, masks)) : "(no statement)";
        statement = masks ? SqlRedaction.WithoutLiterals(statement) : statement;
        if (error.SessionHistory is { } history)
        {
            Lost(logger, error.Display, history, statement);
        }
        else
        {
            Failed(logger, error.Display, error.IsConnectionLost, statement);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Query failed: {Error} (connection lost: {Lost})\n{Statement}")]
    private static partial void Failed(ILogger logger, string error, bool lost, string statement);

    /// <summary>A lost connection with what its session did before (idle time, the last break sent): network or driver.</summary>
    [LoggerMessage(Level = LogLevel.Warning, Message = "Query failed: {Error} (connection lost: True; {History})\n{Statement}")]
    private static partial void Lost(ILogger logger, string error, string history, string statement);
}
