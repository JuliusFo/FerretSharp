using FerretSharp.Core.Connections;
using FerretSharp.Core.Data;
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
    public static Task<DbResult<T>> RunInBackgroundAsync<T>(
        this ShellState shell, ILogger logger, ActiveConnection? connection, string label, long? total, string? title, Guid? workspaceId,
        Func<IProgress<long>, CancellationToken, Task<T>> work) =>
        shell.TrackAsync(label, total, title, workspaceId, (progress, token) => shell.RunDbAsync(logger, connection, () => work(progress, token), token));

    /// <summary>
    /// Like <see cref="RunInBackgroundAsync{T}"/>, but <paramref name="work"/> stays on the caller's thread: a loop over a grid's
    /// own state (its loaded rows, pending changes) whose reads leave the thread themselves – the copy of a marked range read
    /// the grid's row cache on the thread pool while the grid filled it (review of R8).
    /// </summary>
    public static Task<DbResult<T>> CallInBackgroundAsync<T>(
        this ShellState shell, ILogger logger, ActiveConnection? connection, string label, long? total, string? title, Guid? workspaceId,
        Func<IProgress<long>, CancellationToken, Task<T>> work) =>
        shell.TrackAsync(label, total, title, workspaceId, (progress, token) => shell.CallDbAsync(logger, connection, () => work(progress, token)));

    /// <summary>Shows <paramref name="call"/> in the status bar while it runs, with its rows and "Cancel".</summary>
    private static async Task<DbResult<T>> TrackAsync<T>(
        this ShellState shell, string label, long? total, string? title, Guid? workspaceId, Func<IProgress<long>, CancellationToken, Task<DbResult<T>>> call)
    {
        var reads = shell.BackgroundReads;
        var job = reads.Start(label, total, title, workspaceId);
        var progress = new Progress<long>(rows => reads.Report(job, rows)); // reports on the UI thread
        try
        {
            return await call(progress, job.Token);
        }
        finally
        {
            reads.End(job);
        }
    }

    /// <summary>
    /// Every row of <paramref name="read"/> in the background, each handed to <paramref name="add"/> until it says no more (a
    /// limit reached): the values of a column and of whole marked columns (review 3.54.0, B3: the loop stood there twice).
    /// </summary>
    /// <returns>True once read; a cancel or failure comes back in the result.</returns>
    public static Task<DbResult<bool>> ReadAllRowsAsync(
        this ShellState shell, ILogger logger, ActiveConnection? connection, string label, long? total, string? title, Guid? workspaceId,
        RowReader read, Func<RowData, bool> add) =>
        shell.RunInBackgroundAsync(logger, connection, label, total, title, workspaceId, async (progress, token) =>
        {
            long rows = 0;
            await read((block, _, _) =>
            {
                foreach (var row in block)
                {
                    if (!add(row))
                    {
                        return Task.FromResult(false);
                    }
                }

                rows += block.Count;
                progress.Report(rows);
                return Task.FromResult(true);
            }, token);
            return true;
        });
}
