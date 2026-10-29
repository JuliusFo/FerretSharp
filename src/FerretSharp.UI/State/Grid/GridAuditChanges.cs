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

    /// <summary>The keys of the rows the grid holds (its loaded blocks).</summary>
    IEnumerable<RowKey> HeldKeys();

    /// <summary>Runs work outside of a Blazor event on the component (its error boundary sees a failure).</summary>
    void Fire(Func<Task> work);

    void Notify(string text);
}

/// <summary>How a row of an audit table is drawn (WP-63): its cells changed against the version before, and its action.</summary>
/// <param name="Changed">Column indexes; empty for the first version or before the changes are read.</param>
public sealed record AuditMarks(IReadOnlySet<int> Changed, AuditActionKind? Action);

/// <summary>
/// The highlighting of one audit table's grid (WP-63): after each block the grid reads, asks which cells differ from the
/// version before – one query at a time on the workspace's session, like the names beside foreign keys – and redraws the
/// rows whose marks changed (an answer also takes marks away). The action of a row comes from its own value at once. A
/// failed query switches the highlighting off for the tab (told once), as does one that takes longer than
/// <see cref="TimeLimit"/>; a cancelled one is asked once more; <see cref="Clear"/> cancels the running query and forgets
/// what was read. It keeps marks for about as many rows as the grid holds.
/// </summary>
/// <param name="view">What the template says about the audit table.</param>
/// <param name="read">One read on the workspace's session (<see cref="IDataAccess.ReadAuditChangesAsync"/>).</param>
/// <param name="time">The clock of the time limit; the system's by default.</param>
public sealed class GridAuditChanges(
    IAuditChangesGrid grid,
    AuditView view,
    Func<AuditVersions, IReadOnlyList<RowData>, CancellationToken, Task<DbResult<IReadOnlyList<AuditRowChange>>>> read,
    TimeProvider? time = null) : IAsyncDisposable
{
    /// <summary>Marks kept before those of rows the grid no longer holds are dropped: the grid's own limit.</summary>
    public const int MaxKept = RowBlocks.MaxBlocks * IDataAccess.BlockSize;

    /// <summary>
    /// How long one query may take (decision of the user, review 3.54.0, A4): without an index on the base table's key in
    /// the audit table it scans the table (ADR 0036) and holds the workspace's session – paging, writing, commit wait.
    /// </summary>
    public static readonly TimeSpan TimeLimit = TimeSpan.FromSeconds(10);

    private static readonly IReadOnlySet<int> None = new HashSet<int>();

    private readonly Dictionary<RowKey, IReadOnlySet<int>> _changed = [];
    private readonly Queue<(IReadOnlyList<RowData> Rows, int Generation, bool Retried)> _blocks = new();
    private readonly CancellationTokenSource _cancel = new();
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private CancellationTokenSource? _running;
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

    /// <summary>
    /// The grid reads its rows again (F5, after writing, commit or rollback – also in place, when the first block is no
    /// longer held): the versions may differ now, the blocks it reads ask anew. The running query stops – it held the session,
    /// and the grid's new read waited behind it (review 3.54.0, A4).
    /// </summary>
    public void Clear()
    {
        _changed.Clear();
        _blocks.Clear();
        Generation++;
        CancelOffThread.Run(_running);
    }

    /// <summary>Asks for the changes of a block the grid read; one query at a time.</summary>
    public void Queue(IReadOnlyList<RowData> rows)
    {
        if (_disposed || !Compares || rows.Count == 0)
        {
            return;
        }

        _blocks.Enqueue((rows, Generation, false));
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

                using var limit = new CancellationTokenSource(TimeLimit, _time);
                using var running = CancellationTokenSource.CreateLinkedTokenSource(_cancel.Token, limit.Token);
                _running = running;
                DbResult<IReadOnlyList<AuditRowChange>> result;
                try
                {
                    result = await read(view.Versions!, block.Rows, running.Token);
                }
                finally
                {
                    _running = null;
                }

                if (_disposed || !grid.IsAlive || block.Generation != Generation)
                {
                    continue; // read again meanwhile: the new blocks ask again
                }

                if (result.Cancelled && limit.IsCancellationRequested)
                {
                    SwitchOff(TextFormat.Format(GridText.Grid_AuditChangesSlow, (int)TimeLimit.TotalSeconds));
                    continue;
                }

                if (result.Cancelled)
                {
                    if (!block.Retried)
                    {
                        _blocks.Enqueue(block with { Retried = true }); // the session was reset: its rows are still shown
                    }

                    continue;
                }

                if (result.Error is { } error)
                {
                    SwitchOff(error.Display);
                    continue;
                }

                var keys = Apply(block.Rows, result.Value ?? []);
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

    private void SwitchOff(string why)
    {
        _off = true;
        _blocks.Clear();
        grid.Notify(TextFormat.Format(GridText.Grid_AuditChangesOff, why));
    }

    /// <summary>
    /// Takes the answer for <paramref name="rows"/>: a row with changes gets them, every other row of the block loses what it
    /// had (written over, rolled back). Returns the keys whose marks changed.
    /// </summary>
    private HashSet<RowKey> Apply(IReadOnlyList<RowData> rows, IReadOnlyList<AuditRowChange> changes)
    {
        var answered = changes.Where(c => c.Changed.Count > 0).GroupBy(c => c.Key).ToDictionary(g => g.Key, g => g.First().Changed);
        var keys = new HashSet<RowKey>();
        foreach (var key in rows.Select(r => r.Key).Distinct())
        {
            if (answered.TryGetValue(key, out var changed))
            {
                if (!_changed.TryGetValue(key, out var had) || !had.SetEquals(changed))
                {
                    _changed[key] = changed;
                    keys.Add(key);
                }
            }
            else if (_changed.Remove(key))
            {
                keys.Add(key);
            }
        }

        if (_changed.Count > MaxKept)
        {
            var held = grid.HeldKeys().ToHashSet();
            foreach (var gone in _changed.Keys.Where(k => !held.Contains(k)).ToList())
            {
                _changed.Remove(gone);
            }
        }

        return keys;
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
