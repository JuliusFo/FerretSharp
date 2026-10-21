using FerretSharp.Core.Resources;
using FerretSharp.UI.Resources;

namespace FerretSharp.UI.State;

/// <summary>
/// An export being written in the background (A13, WP-54) – or the values of a column read for the clipboard (WP-57): what
/// it is, the rows so far, and how to cancel it.
/// </summary>
public sealed class FileExport
{
    private readonly CancellationTokenSource _cts = new();
    private bool _ended;

    internal FileExport(string label, long? total, string title, Guid? workspaceId)
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
public sealed class FileExports
{
    private readonly List<FileExport> _running = [];

    /// <summary>An export started, made progress or ended.</summary>
    public event Action? Changed;

    public IReadOnlyList<FileExport> Running => _running;

    /// <summary>A running export that reads through the workspace's session; null if none.</summary>
    public FileExport? ReadingIn(Guid workspaceId) => _running.FirstOrDefault(e => e.WorkspaceId == workspaceId);

    /// <param name="label">The file's name (without folder), or what else is read.</param>
    /// <param name="title">The tooltip; null for an export to the file <paramref name="label"/>.</param>
    /// <param name="workspaceId">The workspace whose session reads the rows; null if none is read.</param>
    public FileExport Start(string label, long? total, string? title = null, Guid? workspaceId = null)
    {
        var export = new FileExport(label, total, title ?? TextFormat.Format(ShellText.Status_ExportTitle, label), workspaceId);
        _running.Add(export);
        Changed?.Invoke();
        return export;
    }

    public void Report(FileExport export, long rows)
    {
        if (_running.Contains(export) && rows != export.Rows)
        {
            export.Rows = rows;
            Changed?.Invoke();
        }
    }

    public void End(FileExport export)
    {
        if (_running.Remove(export))
        {
            export.End();
            Changed?.Invoke();
        }
    }
}
