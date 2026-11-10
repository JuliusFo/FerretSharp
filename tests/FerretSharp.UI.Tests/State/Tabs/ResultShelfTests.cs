using FerretSharp.Core.Connections;
using FerretSharp.Core.Data;
using FerretSharp.Core.Query;
using FerretSharp.UI.State;

namespace FerretSharp.UI.Tests.State;

/// <summary>WP-81: the results of a SQL tab – kept ones stay when the next statement runs, the others are replaced.</summary>
public sealed class ResultShelfTests
{
    private static readonly HeldRows Rows = new([], [], Complete: true);

    private static readonly DateTimeOffset At = new(2026, 11, 10, 14, 5, 9, TimeSpan.Zero);

    private static SqlRun Run(string sql = "SELECT * FROM orders") =>
        new(0, new SqlStatement(0, sql.Length, sql), SqlScript.Analyze(sql), new QuerySpec(sql, []), At);

    private static ResultShelf ShelfWith(params SqlRun[] runs)
    {
        var shelf = new ResultShelf();
        foreach (var run in runs)
        {
            shelf.Add(run);
        }

        return shelf;
    }

    [Fact]
    public void A_new_run_replaces_only_the_results_not_kept()
    {
        var (a, b, c) = (Run(), Run(), Run());
        var shelf = ShelfWith(a, b, c);
        Assert.Null(shelf.Keep(b, Rows));
        Assert.Null(shelf.Keep(a, Rows));

        shelf.StartRun();
        var d = Run();
        shelf.Add(d);

        Assert.Equal([b.Id, a.Id, d.Id], shelf.All.Select(r => r.Id)); // kept first, in the order kept
        Assert.Same(Rows, shelf.Kept[0].Held);
        Assert.Equal(d.Id, shelf.Shown?.Id);
        Assert.True(shelf.IsKept(a));
        Assert.False(shelf.IsKept(d));
    }

    /// <summary>Decision of the user: at most ten; the eleventh is refused – none is dropped silently.</summary>
    [Fact]
    public void Only_a_query_that_ran_can_be_kept_and_never_more_than_ten()
    {
        var failed = Run() with { Error = new DatabaseException("ORA-00942") };
        var update = Run("UPDATE orders SET state = 1");
        var shelf = ShelfWith(failed, update);

        Assert.Equal(KeepRefusal.NotAResult, shelf.Keep(failed, Rows));
        Assert.Equal(KeepRefusal.NotAResult, shelf.Keep(update, Rows));
        var twice = Run();
        shelf.Add(twice);
        Assert.Null(shelf.Keep(twice, Rows));
        Assert.Null(shelf.Keep(twice, new HeldRows([], [], Complete: false))); // kept already: nothing changes
        Assert.Same(Rows, shelf.Kept[0].Held);
        shelf.Close(twice);

        for (var i = 0; i < ResultShelf.MaxKept; i++)
        {
            var run = Run();
            shelf.Add(run);
            Assert.Null(shelf.Keep(run, Rows));
        }

        var eleventh = Run();
        shelf.Add(eleventh);
        Assert.Equal(KeepRefusal.Full, shelf.Keep(eleventh, Rows));
        Assert.Equal(ResultShelf.MaxKept, shelf.Kept.Count);
        Assert.Contains(eleventh, shelf.Latest);
    }

    [Fact]
    public void A_result_let_go_stays_with_its_rows_until_the_next_run()
    {
        var (a, b) = (Run(), Run());
        var shelf = ShelfWith(a, b);
        shelf.Keep(a, Rows);

        shelf.Release(a);

        Assert.Empty(shelf.Kept);
        Assert.Equal([a.Id, b.Id], shelf.Latest.Select(r => r.Id));
        Assert.Same(Rows, shelf.Latest[0].Held); // as it was: it does not read again
        shelf.StartRun();
        Assert.Empty(shelf.All);
    }

    [Theory]
    [InlineData(1, 0)] // the one before it
    [InlineData(0, 1)] // the first: the one after it
    public void Closing_the_shown_result_shows_its_neighbour(int closed, int shown)
    {
        var runs = new[] { Run(), Run(), Run() };
        var shelf = ShelfWith(runs);
        shelf.Show(runs[closed]);

        shelf.Close(runs[closed]);

        Assert.Equal(runs[shown].Id, shelf.Shown?.Id);
        Assert.Equal(2, shelf.All.Count);
    }

    /// <summary>While a run starts nothing is chosen: closing a kept result then leaves the newest one shown.</summary>
    [Fact]
    public void Closing_while_a_run_starts_shows_the_last_kept_result()
    {
        var (a, b) = (Run(), Run());
        var shelf = ShelfWith(a, b);
        shelf.Keep(a, Rows);
        shelf.Keep(b, Rows);
        shelf.StartRun();

        shelf.Close(a);

        Assert.Equal(b.Id, shelf.Shown?.Id);
    }

    [Fact]
    public void The_result_shown_is_remembered_across_updates()
    {
        var (a, b) = (Run(), Run());
        var shelf = ShelfWith(a, b);
        shelf.Show(a);

        shelf.Update(a with { Held = Rows });

        Assert.Same(Rows, shelf.Shown?.Held);
    }

    private static readonly IReadOnlyList<SqlColumn> Columns = [];

    private static RowData Row(int n) => new(RowKey.None.Instance, [n]);

    private static SqlPage Page(int from, int count, bool last, DateTimeOffset? asOf = null) =>
        new(Columns, [.. Enumerable.Range(from, count).Select(n => (IReadOnlyList<object?>)[n])], last, TimeSpan.Zero, asOf);

    /// <summary>Rows from the first one on without a gap; the whole result only if every row read is among them.</summary>
    [Theory]
    [InlineData(1000, 1000, true, 1000, true)] // all loaded
    [InlineData(1000, 1500, false, 1000, false)] // more to read
    [InlineData(1000, 1000, true, 800, false)] // more than the limit
    public void The_rows_loaded_from_the_first_on_are_held(int loaded, int rowsLoaded, bool allLoaded, int max, bool complete)
    {
        var held = HeldRows.Of(Columns, null, true, i => i < loaded ? Row(i) : null, rowsLoaded, allLoaded, Page(0, 500, false), null, false, max);

        Assert.Equal((Math.Min(loaded, max), complete), (held.Rows.Count, held.Complete));
    }

    /// <summary>
    /// Review WP-81: scrolled beyond the grid's cache (block 0 gone) or just sorted anew (no block yet), keeping held no
    /// row at all – the statement's first page stands in.
    /// </summary>
    [Fact]
    public void Without_the_first_block_the_first_page_is_held()
    {
        var asOf = DateTimeOffset.Now;

        var held = HeldRows.Of(Columns, null, true, i => i >= 500 ? Row(i) : null, 21_000, false, Page(0, 500, false, asOf), null, false, 10_000);

        Assert.Equal((500, false, asOf), (held.Rows.Count, held.Complete, held.DataAsOf));
        Assert.Equal(0, held.Rows[0].Values[0]);
    }

    [Fact]
    public void Rows_known_in_the_query_s_order_are_held_up_to_the_limit()
    {
        var all = Enumerable.Range(0, 12).Select(Row).ToList();

        Assert.True(HeldRows.Of(Columns, all, true, _ => null, 0, false, null, null, false, 12).Complete);
        Assert.False(HeldRows.Of(Columns, all, false, _ => null, 0, false, null, null, false, 12).Complete); // held rows that were not all
        Assert.Equal(10, HeldRows.Of(Columns, all, true, _ => null, 0, false, null, null, false, 10).Rows.Count);
    }

    /// <summary>Review WP-81: "Load all rows" in a locked workspace lost that its later rows came from another snapshot.</summary>
    [Fact]
    public void Rows_loaded_later_from_another_snapshot_say_so()
    {
        var (first, later) = (DateTimeOffset.Now, DateTimeOffset.Now.AddMinutes(1));
        var held = new HeldRows(Columns, [Row(0)], false, first);

        var same = held.With(Page(1, 2, true, first));
        var moved = held.With(Page(1, 2, false, later));

        Assert.Equal((3, true, first, false), (same.Rows.Count, same.Complete, same.DataAsOf, same.SnapshotMoved));
        Assert.Equal((false, first, true), (moved.Complete, moved.DataAsOf, moved.SnapshotMoved));
    }

    /// <summary>Decision of the user: the table (or the first word) and the time it ran, or a name of the user's.</summary>
    [Fact]
    public void A_kept_result_is_labelled_by_its_table_and_time_or_its_name()
    {
        var run = Run();
        var shelf = ShelfWith(run);
        var time = TimeText.TimeOfDay(At);

        Assert.Equal("Bestellungen · " + time, ResultShelf.Label(run, "Bestellungen"));
        Assert.Equal("SELECT · " + time, ResultShelf.Label(run, null));

        shelf.Rename(run, "  vorher  ");
        Assert.Equal("vorher", ResultShelf.Label(shelf.Shown!, "Bestellungen"));

        shelf.Rename(run, new string('x', 200)); // review: a pasted text is cut
        Assert.Equal(ResultShelf.MaxNameLength, ResultShelf.Label(shelf.Shown!, null).Length);

        shelf.Rename(run, " ");
        Assert.Equal("Bestellungen · " + time, ResultShelf.Label(shelf.Shown!, "Bestellungen"));
    }
}
