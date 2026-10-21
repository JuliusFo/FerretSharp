using FerretSharp.Core.Connections;
using FerretSharp.Core.Resources;
using FerretSharp.UI.Resources;
using Microsoft.Extensions.Logging;

namespace FerretSharp.UI.State;

/// <summary>
/// An export being written in the background (A13, WP-54) – or the values of a column read for the clipboard (WP-57): what
/// it is, the rows so far, and how to cancel it.
/// </summary>
public sealed class BackgroundRead
{
    private readonly CancellationTokenSource _cts = new();
    private bool _ended;

    internal BackgroundRead(string label, long? total, string title, Guid? workspaceId)
    {
        Label = label;
        Total = total;
        Title = title;
        WorkspaceId = workspaceId;
    }

    /// <summary>
    /// The workspace whose session reads the rows (all rows of a query); null if nothing is read (the rows loaded). The read
    /// holds the session until it ends: commit and writes of the workspace say so instead of waiting (review 3.44.0, A2).
    /// </summary>
    public Guid? WorkspaceId { get; }

    /// <summary>What the status bar shows: the file's name (without folder), or the column whose values are copied.</summary>
    public string Label { get; }

    /// <summary>The status bar's tooltip.</summary>
    public string Title { get; }

    /// <summary>Rows expected, if known beforehand (the selected rows, a counted filter).</summary>
    public long? Total { get; }

    public long Rows { get; internal set; }

    public CancellationToken Token => _cts.Token;

    /// <summary>
    /// Cancels the export. The token's registrations run here (the UI thread): the session hands the cancel to ODP.NET on a
    /// pool thread itself (review 3.40.0, A3). A click that arrives after the export ended (the status bar had not drawn
    /// again yet) does nothing.
    /// </summary>
    public void Cancel()
    {
        if (!_ended)
        {
            _cts.Cancel();
        }
    }

    internal void End()
    {
        _ended = true;
        _cts.Dispose();
    }
}

/// <summary>
/// The exports running (A13, WP-54): the status bar shows each with its rows and a cancel button, whichever tab or connection
/// is shown – an export goes on when the user moves elsewhere. Used from the UI thread only.
/// </summary>
public sealed class BackgroundReads
{
    private readonly List<BackgroundRead> _running = [];

    /// <summary>An export started, made progress or ended.</summary>
    public event Action? Changed;

    public IReadOnlyList<BackgroundRead> Running => _running;

    /// <summary>A running export that reads through the workspace's session; null if none.</summary>
    public BackgroundRead? ReadingIn(Guid workspaceId) => _running.FirstOrDefault(e => e.WorkspaceId == workspaceId);

    /// <param name="label">The file's name (without folder), or what else is read.</param>
    /// <param name="title">The tooltip; null for an export to the file <paramref name="label"/>.</param>
    /// <param name="workspaceId">The workspace whose session reads the rows; null if none is read.</param>
    public BackgroundRead Start(string label, long? total, string? title = null, Guid? workspaceId = null)
    {
        var export = new BackgroundRead(label, total, title ?? TextFormat.Format(ShellText.Status_ExportTitle, label), workspaceId);
        _running.Add(export);
        Changed?.Invoke();
        return export;
    }

    public void Report(BackgroundRead export, long rows)
    {
        if (_running.Contains(export) && rows != export.Rows)
        {
            export.Rows = rows;
            Changed?.Invoke();
        }
    }

    public void End(BackgroundRead export)
    {
        if (_running.Remove(export))
        {
            export.End();
            Changed?.Invoke();
        }

        if (_running.Count == 0)
        {
            _idle?.TrySetResult();
            _idle = null;
        }
    }

    private TaskCompletionSource? _idle;

    /// <summary>
    /// Cancels every export and waits until they ended – each removes its temporary file –, at most <paramref name="max"/>: a
    /// read over a silent VPN may not return on a cancel (quitting, review 3.44.0, A5).
    /// </summary>
    public async Task CancelAllAsync(TimeSpan max)
    {
        if (_running.Count == 0)
        {
            return;
        }

        _idle ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var idle = _idle.Task;
        foreach (var export in _running.ToList())
        {
            export.Cancel();
        }

        try
        {
            await idle.WaitAsync(max);
        }
        catch (TimeoutException)
        {
            // what still runs ends with the app
        }
    }
}

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
