using FerretSharp.Core.Connections;
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

    [Fact]
    public void The_result_shown_is_remembered_across_updates()
    {
        var (a, b) = (Run(), Run());
        var shelf = ShelfWith(a, b);
        shelf.Show(a);

        shelf.Update(a with { Held = Rows });

        Assert.Same(Rows, shelf.Shown?.Held);
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

        shelf.Rename(run, " ");
        Assert.Equal("Bestellungen · " + time, ResultShelf.Label(shelf.Shown!, "Bestellungen"));
    }
}
