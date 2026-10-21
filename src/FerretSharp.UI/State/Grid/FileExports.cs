namespace FerretSharp.UI.State;

/// <summary>An export being written in the background (A13, WP-54): its file, the rows so far, and how to cancel it.</summary>
public sealed class FileExport
{
    private readonly CancellationTokenSource _cts = new();
    private bool _ended;

    internal FileExport(string fileName, long? total)
    {
        FileName = fileName;
        Total = total;
    }

    /// <summary>The file's name (without folder), as the status bar shows it.</summary>
    public string FileName { get; }

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

    public FileExport Start(string fileName, long? total)
    {
        var export = new FileExport(fileName, total);
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
