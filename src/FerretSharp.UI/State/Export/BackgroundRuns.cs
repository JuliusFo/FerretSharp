using FerretSharp.Core.Connections;
using Microsoft.Extensions.Logging;

namespace FerretSharp.UI.State;

/// <summary>
/// A read in the background (review 3.44.0, B3: one way for "all rows" as a file and "all values" for the clipboard): shown in
/// the status bar with its rows and a cancel button while it runs; errors, refusals and the cancel come back as a
/// <see cref="DbResult{T}"/> like any database call.
/// </summary>
public static class BackgroundRuns
{
    /// <param name="label">What the status bar shows (the file's name, the column).</param>
    /// <param name="title">The tooltip; null for an export to the file <paramref name="label"/>.</param>
    /// <param name="workspaceId">The workspace whose session reads; null if none is read.</param>
    /// <param name="work">The read, reporting its rows (on the UI thread) and stopping at the token.</param>
    public static async Task<DbResult<T>> RunInBackgroundAsync<T>(
        this ShellState shell, ILogger logger, ActiveConnection? connection, string label, long? total, string? title, Guid? workspaceId,
        Func<IProgress<long>, CancellationToken, Task<T>> work)
    {
        var reads = shell.BackgroundReads;
        var job = reads.Start(label, total, title, workspaceId);
        var progress = new Progress<long>(rows => reads.Report(job, rows)); // reports on the UI thread
        try
        {
            var token = job.Token;
            return await shell.RunDbAsync(logger, connection, () => work(progress, token), token);
        }
        finally
        {
            reads.End(job);
        }
    }
}
