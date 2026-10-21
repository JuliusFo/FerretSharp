namespace FerretSharp.UI.State;

/// <summary>
/// The rows a table grid is loading (fix 3.45.1): one block at a time (grid.js asks for one after the other), shown with a
/// clock and "Cancel" once it takes longer than <see cref="ShowAfter"/>. Before, a slow first block left an empty grid and
/// "≥ 0 rows" for minutes, and nothing could stop it. A new query (filter, sort, F5, reload after writing) supersedes the
/// running load: it is cancelled – it would hold the workspace's session, and the new query waited behind it – and its
/// result no longer counts, even if it arrives (it would have put rows of the old query into the held blocks).
/// </summary>
/// <param name="time">The clock of <see cref="ShowAfter"/> (tests); the system's if null.</param>
public sealed class PageLoads(TimeProvider? time = null)
{
    /// <summary>How long a load runs before it is shown: a quick one would only flicker.</summary>
    public static readonly TimeSpan ShowAfter = TimeSpan.FromMilliseconds(400);

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>The load running now; null if none.</summary>
    public PageLoad? Running { get; private set; }

    /// <summary>The running load once it is shown (<see cref="WaitToShowAsync"/>); null if none or not yet.</summary>
    public PageLoad? Shown => Running is { Shown: true } load ? load : null;

    /// <summary>Starts loading a block; a load still running is superseded.</summary>
    /// <param name="firstPage">The block at row 0: the rows of a new query (the footer has no count yet).</param>
    /// <param name="query">What the block is of (the grid's sort order), for <see cref="Supersede"/>.</param>
    public PageLoad Start(bool firstPage, string query = "")
    {
        Supersede();
        return Running = new PageLoad(_time.GetLocalNow(), firstPage, query);
    }

    /// <summary>Waits <see cref="ShowAfter"/>; true if <paramref name="load"/> still runs then – it is shown from now on.</summary>
    public async Task<bool> WaitToShowAsync(PageLoad load)
    {
        try
        {
            await Task.Delay(ShowAfter, _time, load.Token);
        }
        catch (OperationCanceledException)
        {
            return false;
        }

        if (!ReferenceEquals(Running, load))
        {
            return false;
        }

        load.Shown = true;
        return true;
    }

    /// <summary>"Cancel": the running load stops; its end says so (<see cref="PageLoad.CancelledByUser"/>).</summary>
    public void Cancel()
    {
        if (Running is { } load)
        {
            load.CancelledByUser = true;
            load.Cancel();
        }
    }

    /// <summary>A new query or the grid goes away: the running load stops, and its result no longer counts.</summary>
    /// <param name="keep">
    /// The new query: a load already of it stays. The grid reports a sort click after it may have asked for the new order's
    /// first block – superseding that one left the grid empty.
    /// </param>
    public void Supersede(string? keep = null)
    {
        if (Running is { } load && load.Query != keep)
        {
            Running = null;
            load.Cancel();
        }
    }

    /// <summary>The load ended (rows, error or cancel).</summary>
    /// <returns>True if its result counts; false if it was superseded meanwhile.</returns>
    public bool End(PageLoad load)
    {
        var current = ReferenceEquals(Running, load);
        if (current)
        {
            Running = null;
        }

        load.Dispose();
        return current;
    }
}

/// <summary>One block being loaded (<see cref="PageLoads"/>).</summary>
public sealed class PageLoad : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private bool _disposed;

    internal PageLoad(DateTimeOffset started, bool firstPage, string query)
    {
        Started = started;
        FirstPage = firstPage;
        Query = query;
        Token = _cts.Token;
    }

    public DateTimeOffset Started { get; }

    /// <inheritdoc cref="PageLoads.Start"/>
    public string Query { get; }

    /// <inheritdoc cref="PageLoads.Start"/>
    public bool FirstPage { get; }

    public CancellationToken Token { get; }

    /// <summary>Ran longer than <see cref="PageLoads.ShowAfter"/>: the grid shows it.</summary>
    public bool Shown { get; internal set; }

    /// <summary>Stopped by "Cancel", not by a new query.</summary>
    public bool CancelledByUser { get; internal set; }

    internal void Cancel()
    {
        if (!_disposed)
        {
            _cts.Cancel();
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _cts.Dispose();
    }
}
