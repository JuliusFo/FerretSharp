using FerretSharp.Core.Data;

namespace FerretSharp.UI.State;

/// <summary>
/// Rows of a query result held in memory (WP-81): kept with the result, or all loaded at once. The grid shows and sorts
/// only these – it never reads the query again, so the rows stay as they were read.
/// </summary>
/// <param name="Columns">The result's columns.</param>
/// <param name="Rows">From the first row on, in the order the grid showed them.</param>
/// <param name="Complete">The rows are the whole result; false if it had more (beyond <see cref="ResultShelf.MaxRows"/> or not loaded).</param>
/// <param name="DataAsOf">Snapshot of a locked workspace the rows came from (of their first row); null outside one.</param>
/// <param name="SnapshotMoved">Later rows came from another snapshot than the first (<see cref="Snapshots.Next"/>).</param>
public sealed record HeldRows(
    IReadOnlyList<SqlColumn> Columns, IReadOnlyList<RowData> Rows, bool Complete, DateTimeOffset? DataAsOf = null, bool SnapshotMoved = false)
{
    /// <summary>
    /// The rows a grid can hold now, at most <paramref name="max"/>: every row in the query's own order if all are known,
    /// else the rows loaded from the first one on without a gap. Fewer than the first page – its block went from the
    /// grid's cache after scrolling far, or a new order was not read yet – takes the first page as read when the statement
    /// ran (review WP-81: keeping held no rows at all).
    /// </summary>
    /// <param name="all">Every row in the query's own order, if known (<see cref="ResultOrder.All"/>).</param>
    /// <param name="allComplete">Whether <paramref name="all"/> is the whole result (held rows may not be).</param>
    /// <param name="loaded">The loaded row at a grid index, null if its block is not held.</param>
    /// <param name="rowsLoaded">Rows read so far; with <paramref name="allLoaded"/> the result's size.</param>
    /// <param name="firstPage">The statement's first page, read when it ran; null if there is none.</param>
    public static HeldRows Of(
        IReadOnlyList<SqlColumn> columns, IReadOnlyList<RowData>? all, bool allComplete, Func<int, RowData?> loaded, int rowsLoaded,
        bool allLoaded, SqlPage? firstPage, DateTimeOffset? dataAsOf, bool snapshotMoved, int max)
    {
        ArgumentNullException.ThrowIfNull(loaded);
        if (all is not null)
        {
            return new HeldRows(columns, [.. all.Take(max)], allComplete && all.Count <= max, dataAsOf, snapshotMoved);
        }

        var rows = new List<RowData>();
        while (rows.Count < max && loaded(rows.Count) is { } row)
        {
            rows.Add(row);
        }

        if (firstPage is { } first && rows.Count < Math.Min(first.Rows.Count, max))
        {
            return new HeldRows(columns, [.. first.Rows.Take(max).Select(values => new RowData(RowKey.None.Instance, values))],
                first.IsLastPage && first.Rows.Count <= max, first.DataAsOf);
        }

        return new HeldRows(columns, rows, allLoaded && rows.Count == rowsLoaded, dataAsOf, snapshotMoved);
    }

    /// <summary>
    /// These rows and the ones read after them ("Load all rows"): complete if that was the last page; a page from another
    /// snapshot than the first rows says so (review WP-81: it was lost).
    /// </summary>
    public HeldRows With(SqlPage next)
    {
        ArgumentNullException.ThrowIfNull(next);
        var (asOf, moved) = Snapshots.Next(Rows.Count, DataAsOf, SnapshotMoved, next.DataAsOf);
        return this with
        {
            Rows = [.. Rows, .. next.Rows.Select(values => new RowData(RowKey.None.Instance, values))],
            Complete = next.IsLastPage,
            DataAsOf = asOf,
            SnapshotMoved = moved,
        };
    }
}

/// <summary>Why a result could not be kept.</summary>
public enum KeepRefusal
{
    /// <summary>Not a query that ran without failure: there is nothing to keep.</summary>
    NotAResult,

    /// <summary><see cref="ResultShelf.MaxKept"/> results are kept already.</summary>
    Full,
}

/// <summary>
/// The results of a SQL tab (WP-81): those the user keeps, then those of the last run. A new run replaces only the latter.
/// Kept results stay in the order they were kept; the one shown is remembered by <see cref="SqlRun.Id"/>. Not saved.
/// </summary>
public sealed class ResultShelf
{
    /// <summary>Results kept per tab at most (decision of the user): one more is refused, never one dropped silently.</summary>
    public const int MaxKept = 10;

    /// <summary>Rows a result holds in memory at most (decision of the user).</summary>
    public const int MaxRows = 10_000;

    private readonly List<SqlRun> _kept = [];
    private readonly List<SqlRun> _latest = [];
    private Guid? _shown;

    /// <summary>The kept results, in the order they were kept.</summary>
    public IReadOnlyList<SqlRun> Kept => _kept;

    /// <summary>The statements of the last run (one for Ctrl+Enter, several for a script), and results let go since.</summary>
    public IReadOnlyList<SqlRun> Latest => _latest;

    /// <summary>Every result in the order of the bar: the kept ones first.</summary>
    public IReadOnlyList<SqlRun> All => [.. _kept, .. _latest];

    /// <summary>The result shown: the one chosen, else the newest of the last run, else the last kept one.</summary>
    public SqlRun? Shown => Find(_shown) ?? (_latest.Count > 0 ? _latest[^1] : _kept.Count > 0 ? _kept[^1] : null);

    public bool IsKept(SqlRun run) => _kept.Exists(r => r.Id == run.Id);

    /// <summary>Only a query that ran without failure has rows to keep.</summary>
    public static bool CanKeep(SqlRun run) => run is { Info.IsQuery: true, Error: null, Cancelled: false };

    /// <summary>A run starts: the results of the last one go, the kept ones stay.</summary>
    public void StartRun()
    {
        _latest.Clear();
        _shown = null;
    }

    /// <summary>A statement of the run ended: it is shown.</summary>
    public void Add(SqlRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        _latest.Add(run);
        _shown = run.Id;
    }

    public void Show(SqlRun run) => _shown = Find(run.Id)?.Id ?? _shown;

    /// <summary>The result with new values (rows loaded, renamed) in its place.</summary>
    public void Update(SqlRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        foreach (var list in (List<SqlRun>[])[_kept, _latest])
        {
            var index = list.FindIndex(r => r.Id == run.Id);
            if (index >= 0)
            {
                list[index] = run;
            }
        }
    }

    /// <summary>
    /// Keeps a result of the last run with its rows as they are now; it moves behind the kept ones. Null if kept (or kept
    /// already), else why not.
    /// </summary>
    public KeepRefusal? Keep(SqlRun run, HeldRows rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (IsKept(run))
        {
            return null;
        }

        var index = _latest.FindIndex(r => r.Id == run.Id);
        if (index < 0 || !CanKeep(_latest[index]))
        {
            return KeepRefusal.NotAResult;
        }

        if (_kept.Count >= MaxKept)
        {
            return KeepRefusal.Full;
        }

        _kept.Add(_latest[index] with { Held = rows });
        _latest.RemoveAt(index);
        return null;
    }

    /// <summary>
    /// Lets a kept result go: it stays in front of the last run's results until the next run replaces them – with its rows
    /// as they were, it does not read again.
    /// </summary>
    public void Release(SqlRun run)
    {
        var index = _kept.FindIndex(r => r.Id == run.Id);
        if (index >= 0)
        {
            _latest.Insert(0, _kept[index]);
            _kept.RemoveAt(index);
        }
    }

    /// <summary>Removes a result; if it was shown, its neighbour is (the one before it in the bar, else the one after).</summary>
    public void Close(SqlRun run)
    {
        var all = All;
        var index = all.ToList().FindIndex(r => r.Id == run.Id);
        if (index < 0)
        {
            return;
        }

        _kept.RemoveAll(r => r.Id == run.Id);
        _latest.RemoveAll(r => r.Id == run.Id);
        if (_shown == run.Id || Find(_shown) is null)
        {
            _shown = index > 0 ? all[index - 1].Id : all.Count > 1 ? all[1].Id : null;
        }
    }

    /// <summary>A name of the user's for the result; blank goes back to the label made of table and time.</summary>
    public void Rename(SqlRun run, string? name)
    {
        if (Find(run.Id) is { } current)
        {
            Update(current with { Name = string.IsNullOrWhiteSpace(name) ? null : name.Trim() });
        }
    }

    private SqlRun? Find(Guid? id) => id is null ? null : _kept.Find(r => r.Id == id) ?? _latest.Find(r => r.Id == id);

    /// <summary>
    /// The label of a kept result in the bar (decision of the user): its name, else the table it reads (or the statement's
    /// first word) and the time it ran.
    /// </summary>
    /// <param name="table">The single table the query reads, as shown; null if it reads none or several.</param>
    public static string Label(SqlRun run, string? table)
    {
        ArgumentNullException.ThrowIfNull(run);
        return run.Name ?? (table ?? run.Info.FirstWord) + " · " + TimeText.TimeOfDay(run.At);
    }
}
