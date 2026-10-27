using FerretSharp.Core.Audit;
using FerretSharp.Core.Data;
using FerretSharp.Core.Resources;
using FerretSharp.UI.Resources;

namespace FerretSharp.UI.State;

/// <summary>What <see cref="GridAuditChanges"/> needs of its grid.</summary>
public interface IAuditChangesGrid
{
    /// <summary>The grid was created and its component still lives: only then are changes read and shown.</summary>
    bool IsAlive { get; }

    /// <summary>Redraws the held rows with these keys.</summary>
    Task RedrawAsync(IReadOnlySet<RowKey> keys);

    /// <summary>Runs work outside of a Blazor event on the component (its error boundary sees a failure).</summary>
    void Fire(Func<Task> work);

    void Notify(string text);
}

/// <summary>How a row of an audit table is drawn (WP-63): its cells changed against the version before, and its action.</summary>
/// <param name="Changed">Column indexes; empty for the first version or before the changes are read.</param>
public sealed record AuditMarks(IReadOnlySet<int> Changed, AuditActionKind? Action);

/// <summary>
/// The highlighting of one audit table's grid (WP-63): after each block the grid reads, asks which cells differ from the
/// version before – one query at a time on the workspace's session, like the names beside foreign keys – and redraws those
/// rows. The action of a row comes from its own value at once. A failed query switches the highlighting off for the tab
/// (told once); reading from the first row again forgets what was read.
/// </summary>
/// <param name="view">What the template says about the audit table.</param>
/// <param name="read">One read on the workspace's session (<see cref="IDataAccess.ReadAuditChangesAsync"/>).</param>
public sealed class GridAuditChanges(
    IAuditChangesGrid grid,
    AuditView view,
    Func<AuditVersions, IReadOnlyList<RowData>, CancellationToken, Task<DbResult<IReadOnlyList<AuditRowChange>>>> read) : IAsyncDisposable
{
    private static readonly IReadOnlySet<int> None = new HashSet<int>();

    private readonly Dictionary<RowKey, IReadOnlySet<int>> _changed = [];
    private readonly Queue<(IReadOnlyList<RowData> Rows, int Generation)> _blocks = new();
    private readonly CancellationTokenSource _cancel = new();
    private bool _reading;
    private bool _off;
    private bool _disposed;

    public AuditView View => view;

    /// <summary>Counts reads from the first row: an answer for an older generation is not applied.</summary>
    public int Generation { get; private set; }

    /// <summary>Whether cells are compared: the template orders the versions, and no query has failed.</summary>
    public bool Compares => view.Versions is not null && !_off;

    /// <summary>How the row with <paramref name="key"/> and <paramref name="values"/> is drawn; null if nothing marks it.</summary>
    public AuditMarks? MarksOf(RowKey? key, IReadOnlyList<object?>? values)
    {
        var changed = key is not null && _changed.TryGetValue(key, out var found) ? found : None;
        var action = values is null ? null : view.ActionOf(values);
        return changed.Count == 0 && action is null ? null : new AuditMarks(changed, action);
    }

    /// <summary>The grid reads from its first row again (reload, F5, after writing): the versions may differ now.</summary>
    public void Clear()
    {
        _changed.Clear();
        _blocks.Clear();
        Generation++;
    }

    /// <summary>Asks for the changes of a block the grid read; one query at a time.</summary>
    public void Queue(IReadOnlyList<RowData> rows)
    {
        if (_disposed || !Compares || rows.Count == 0)
        {
            return;
        }

        _blocks.Enqueue((rows, Generation));
        if (!_reading)
        {
            grid.Fire(RunAsync);
        }
    }

    private async Task RunAsync()
    {
        if (_reading)
        {
            return;
        }

        _reading = true;
        try
        {
            while (!_disposed && grid.IsAlive && Compares && _blocks.TryDequeue(out var block))
            {
                if (block.Generation != Generation)
                {
                    continue;
                }

                var result = await read(view.Versions!, block.Rows, _cancel.Token);
                if (_disposed || !grid.IsAlive || block.Generation != Generation || result.Cancelled)
                {
                    continue; // read from the first row meanwhile, or the session was reset: the new blocks ask again
                }

                if (result.Error is { } error)
                {
                    _off = true;
                    _blocks.Clear();
                    grid.Notify(TextFormat.Format(GridText.Grid_AuditChangesOff, error.Display));
                    continue;
                }

                var keys = new HashSet<RowKey>();
                foreach (var change in result.Value ?? [])
                {
                    if (change.Changed.Count > 0)
                    {
                        _changed[change.Key] = change.Changed;
                        keys.Add(change.Key);
                    }
                }

                if (keys.Count > 0)
                {
                    await grid.RedrawAsync(keys);
                }
            }
        }
        finally
        {
            _reading = false;
            if (_disposed)
            {
                _cancel.Dispose();
            }
        }
    }

    /// <summary>A running query stops, queued ones are not run; the token source goes once the last one ended.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _blocks.Clear();
        await _cancel.CancelAsync();
        if (!_reading)
        {
            _cancel.Dispose();
        }
    }
}
