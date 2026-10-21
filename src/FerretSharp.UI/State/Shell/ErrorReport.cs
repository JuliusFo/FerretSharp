using FerretSharp.Core.Connections;
using FerretSharp.Core.Query;

namespace FerretSharp.UI.State;

/// <summary>What "Copy" in the error dialog puts on the clipboard.</summary>
public static class ErrorReport
{
    /// <summary>
    /// The error and its statement with the binds as the dialog shows them – masked on Prod (review 3.40.0, A8) – and
    /// passwords blacked out (A24: the clipboard reaches the Windows history and the cloud sync). Out of the dialog, with a
    /// test (review 3.44.0, C1).
    /// </summary>
    /// <param name="mask">Hide bind values (the connection the statement ran on is Prod).</param>
    public static string Text(DatabaseException error, bool mask)
    {
        ArgumentNullException.ThrowIfNull(error);
        return error.Display + (error.Statement is { } statement
            ? Environment.NewLine + Environment.NewLine + SqlRedaction.Redact(BindValues.Describe(statement, mask))
            : "");
    }
}
