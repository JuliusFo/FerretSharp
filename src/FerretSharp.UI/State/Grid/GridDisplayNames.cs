using FerretSharp.Core.Data;
using FerretSharp.Core.Display;
using FerretSharp.Core.Resources;
using FerretSharp.Core.Schema;
using FerretSharp.UI.Resources;

namespace FerretSharp.UI.State;

/// <summary>What <see cref="GridDisplayNames"/> needs of its grid.</summary>
public interface IDisplayNamesGrid
{
    /// <summary>The grid was created and its component still lives: only then are names planned, looked up and shown.</summary>
    bool IsAlive { get; }

    /// <summary>The values of every row the grid holds (loaded blocks and new rows), as it shows them.</summary>
    IEnumerable<IReadOnlyList<object?>> HeldRows();

    /// <summary>The column metadata again (badges, which columns show names); rows redrawn from the held values.</summary>
    Task RelabelAsync();

    /// <summary>Redraws every held row.</summary>
    Task RedrawAllAsync();

    /// <summary>Redraws the held rows whose values <paramref name="touches"/> selects.</summary>
    Task RedrawAsync(Func<IReadOnlyList<object?>, bool> touches);

    /// <summary>Runs work outside of a Blazor event on the component (its error boundary sees a failure).</summary>
    void Fire(Func<Task> work);

    void Notify(string text);
}

/// <summary>
/// The names beside the foreign key values of one table grid (WP-31; R5b, review 3.31.0 B1: ~260 lines of
/// <c>FerretGrid</c> without tests): plans which columns get names, asks for the names the held rows need – one lookup at a
/// time on the workspace's session –, tries a slow lookup again and switches a column off that keeps failing. A plan
/// that finishes after a newer one, and a lookup planned before the grid read from its first row again, do nothing.
/// </summary>
/// <param name="plan">The links of the table's columns (DisplayLinks.PlanAsync); none if that failed.</param>
/// <param name="lookup">One lookup on the workspace's session, with its time limit (DisplayNames.LookupAsync).</param>
/// <param name="format">A new formatter for the names of a plan: a value of a target's column as text.</param>
/// <param name="columnName">The name of a column of the grid's table, for the notice that it shows no names any more.</param>
public sealed class GridDisplayNames(
    IDisplayNamesGrid grid,
    Func<Task<IReadOnlyList<DisplayLink>>> plan,
    Func<DisplayLookup, CancellationToken, Task<DbResult<IReadOnlyList<LookupRow>?>>> lookup,
    Func<Func<TableDetails, int, object?, string?>> format,
    Func<int, string> columnName) : IAsyncDisposable
{
    private readonly Queue<(DisplayLookup Lookup, DisplayNames Names, int Generation)> _lookups = new();
    private readonly CancellationTokenSource _cancel = new();
    private int _planSequence;
    private bool _lookingUp;
    private bool _disposed;

    /// <summary>What was looked up for the grid's foreign key columns; replaced when the links change. The form shows them too.</summary>
    public DisplayNames Names { get; private set; } = DisplayNames.None;

    /// <summary>Counts reads from the first row: a lookup of an older generation is not applied.</summary>
    public int Generation { get; private set; }

    /// <summary>
    /// Finds the foreign key columns whose target has a display column; if that changed (or <paramref name="force"/>),
    /// starts over with new names and looks up the values of the rows the grid holds. Nothing once the grid is gone
    /// (R5b: a plan after the dispose still asked the database).
    /// </summary>
    public async Task PlanAsync(bool force = false)
    {
        if (_disposed || !grid.IsAlive)
        {
            return;
        }

        var sequence = ++_planSequence;
        var links = await plan();
        if (_disposed || !grid.IsAlive || sequence != _planSequence || (!force && SameLinks(links, Names.Links)))
        {
            return;
        }

        Names = links.Count == 0 ? DisplayNames.None : new DisplayNames(links, format());
        Generation++;
        await grid.RelabelAsync();
        await grid.RedrawAllAsync();
        Queue(grid.HeldRows());
    }

    /// <summary>The grid reads from its first row again (reload, F5, after writing): the names may read differently now.</summary>
    public void Clear()
    {
        Names.Clear();
        Generation++;
    }

    /// <summary>Asks for the names the rows need and are not known yet; one lookup at a time.</summary>
    public void Queue(IEnumerable<IReadOnlyList<object?>> rows)
    {
        if (_disposed || Names.IsEmpty)
        {
            return;
        }

        foreach (var missing in Names.Missing(rows))
        {
            _lookups.Enqueue((missing, Names, Generation));
        }

        if (_lookups.Count > 0 && !_lookingUp)
        {
            grid.Fire(RunLookupsAsync);
        }
    }

    /// <summary>
    /// Runs the queued lookups one after the other and redraws the rows they were for. A lookup planned before the grid
    /// read from its first row again (or before the links changed) is dropped unrun.
    /// </summary>
    private async Task RunLookupsAsync()
    {
        if (_lookingUp)
        {
            return;
        }

        _lookingUp = true;
        try
        {
            while (!_disposed && grid.IsAlive && _lookups.TryDequeue(out var queued))
            {
                var (asked, names, generation) = queued;
                if (!ReferenceEquals(names, Names) || generation != Generation || !names.Wants(asked))
                {
                    continue;
                }

                var result = await lookup(asked, _cancel.Token);
                if (_disposed || !grid.IsAlive || !ReferenceEquals(names, Names))
                {
                    continue;
                }

                if (generation != Generation)
                {
                    continue; // read from the first row meanwhile: Clear forgot the keys, the new blocks ask again
                }

                if (result.Cancelled)
                {
                    names.Dropped(asked); // session reset or closing: the keys may be asked for again
                }
                else if (result.Error is { } error)
                {
                    await SwitchOffAsync(asked, error.Display);
                }
                else if (result.Value is not { } rows)
                {
                    var reason = TextFormat.Format(GridText.Grid_DisplayLookupTimeout,
                        asked.Source.Target.Table.DisplayName, (int)DisplayNames.LookupTimeout.TotalSeconds);
                    if (names.TimedOut(asked, reason))
                    {
                        await SwitchOffAsync(asked, reason);
                    }
                    else
                    {
                        _lookups.Enqueue(queued); // the session may just have been busy: try again after the others
                    }
                }
                else
                {
                    names.Found(asked, rows);
                    await grid.RedrawAsync(values => names.Touches(asked, values));
                    Queue([]); // names one level deeper (WP-65): the values just found are looked up there
                }
            }
        }
        finally
        {
            _lookingUp = false;
            if (_disposed)
            {
                _cancel.Dispose(); // the last lookup has ended (R5b: never disposed)
            }
        }
    }

    /// <summary>
    /// The column shows no names in this tab any more – or, one level deeper (WP-65), its names show the value of that part
    /// instead of the name found for it: told once, the rows are redrawn.
    /// </summary>
    private async Task SwitchOffAsync(DisplayLookup failed, string reason)
    {
        Names.Failed(failed, reason);
        if (failed.Part is null)
        {
            grid.Notify(TextFormat.Format(GridText.Grid_DisplayLinkOff, columnName(failed.Link.Column), reason));
            await grid.RelabelAsync();
        }
        else
        {
            grid.Notify(TextFormat.Format(GridText.Grid_DisplayPartOff, columnName(failed.Link.Column), failed.Source.Target.Table.DisplayName, reason));
        }

        await grid.RedrawAllAsync();
    }

    /// <summary>The same columns over the same keys to the same display columns (one level deeper too).</summary>
    private static bool SameLinks(IReadOnlyList<DisplayLink> a, IReadOnlyList<DisplayLink> b) =>
        a.Count == b.Count && a.Zip(b).All(p => p.First.SameAs(p.Second));

    /// <summary>A running lookup stops, queued ones are not run; the token source goes once the last lookup ended.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lookups.Clear();
        await _cancel.CancelAsync();
        if (!_lookingUp)
        {
            _cancel.Dispose();
        }
    }
}
