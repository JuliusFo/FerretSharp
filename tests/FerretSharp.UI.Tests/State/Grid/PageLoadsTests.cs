using FerretSharp.UI.State;
using Microsoft.Extensions.Time.Testing;

namespace FerretSharp.UI.Tests.State;

/// <summary>
/// Fix 3.45.1: a slow first block of a table left an empty grid and "≥ 0 rows" for minutes, without any sign that it was
/// loading and without a way to stop it. A load is shown after a moment, can be cancelled, and a new query supersedes it.
/// </summary>
public sealed class PageLoadsTests
{
    private readonly FakeTimeProvider _time = new();

    [Fact]
    public async Task A_quick_load_is_never_shown()
    {
        var loads = new PageLoads(_time);
        var load = loads.Start(firstPage: true);
        var showing = loads.WaitToShowAsync(load);

        Assert.True(loads.End(load));
        _time.Advance(PageLoads.ShowAfter);

        Assert.False(await showing);
        Assert.Null(loads.Shown);
        Assert.Null(loads.Running);
    }

    [Fact]
    public async Task A_slow_load_is_shown_until_it_ends()
    {
        var loads = new PageLoads(_time);
        var load = loads.Start(firstPage: true);
        var showing = loads.WaitToShowAsync(load);
        Assert.Null(loads.Shown);

        _time.Advance(PageLoads.ShowAfter);

        Assert.True(await showing);
        Assert.Same(load, loads.Shown);
        Assert.True(load.FirstPage);
        Assert.Equal(_time.GetLocalNow() - PageLoads.ShowAfter, load.Started);

        Assert.True(loads.End(load));
        Assert.Null(loads.Shown);
    }

    [Fact]
    public void Cancel_stops_the_running_load_and_its_result_still_counts()
    {
        var loads = new PageLoads(_time);
        var load = loads.Start(firstPage: false);

        loads.Cancel();

        Assert.True(load.Token.IsCancellationRequested);
        Assert.True(load.CancelledByUser);
        Assert.True(loads.End(load)); // the grid says it was cancelled
    }

    [Fact]
    public async Task A_new_query_supersedes_the_running_load()
    {
        var loads = new PageLoads(_time);
        var old = loads.Start(firstPage: true);
        var oldShowing = loads.WaitToShowAsync(old);

        var current = loads.Start(firstPage: true);

        Assert.True(old.Token.IsCancellationRequested); // it would hold the session: the new query waited behind it
        Assert.False(old.CancelledByUser);
        Assert.False(await oldShowing);
        Assert.False(current.Token.IsCancellationRequested);
        Assert.Same(current, loads.Running);

        // Its rows arriving late must not count: they belong to the old filter.
        Assert.False(loads.End(old));
        Assert.Same(current, loads.Running);
        Assert.True(loads.End(current));
    }

    [Fact]
    public void Superseding_from_outside_stops_the_load_before_the_grid_asks_again()
    {
        var loads = new PageLoads(_time);
        var load = loads.Start(firstPage: false);

        loads.Supersede(); // F5, filter, reload after writing, the tab closes

        Assert.True(load.Token.IsCancellationRequested);
        Assert.Null(loads.Running);
        Assert.False(loads.End(load));
    }

    [Fact]
    public void A_sort_click_supersedes_only_a_load_of_another_order()
    {
        var loads = new PageLoads(_time);
        var old = loads.Start(firstPage: true, "c0 asc");

        loads.Supersede(keep: "c2 desc");
        Assert.True(old.Token.IsCancellationRequested);

        // The grid asked for the new order's first block before it reported the click: that block stays.
        var current = loads.Start(firstPage: true, "c2 desc");
        loads.Supersede(keep: "c2 desc");
        Assert.False(current.Token.IsCancellationRequested);
        Assert.Same(current, loads.Running);
    }

    [Fact]
    public void Cancel_without_a_running_load_does_nothing()
    {
        var loads = new PageLoads(_time);
        var load = loads.Start(firstPage: true);
        loads.End(load);

        loads.Cancel(); // no ObjectDisposedException from the ended load's source
        loads.Supersede();

        Assert.False(load.CancelledByUser);
    }
}
