using FerretSharp.Core.Connections;
using FerretSharp.Core.Query;
using FerretSharp.Core.Schema;
using FerretSharp.UI.State;
using Microsoft.Extensions.Time.Testing;

namespace FerretSharp.UI.Tests.State;

/// <summary>
/// The counts behind incoming FK jumps share the workspace's session with the jump's first page: one at a time, all at once
/// or only the one the mouse rests on (<see cref="Core.Settings.FkCountMode"/>), and nothing left loading after a cancel.
/// </summary>
public sealed class FkCountsTests
{
    private static readonly TableRef Kunde = new("APP", "KUNDE");

    private static FkJump Incoming(string table) => new(
        new ForeignKeyInfo("FK_" + table, new("APP", table), ["KUNDE_ID"], Kunde, ["ID"], FkSource.Declared),
        JumpDirection.Incoming, new("APP", table), [], null);

    private static readonly FkJump Auftrag = Incoming("AUFTRAG");
    private static readonly FkJump Rechnung = Incoming("RECHNUNG");
    private static readonly FkJump Adresse = Incoming("ADRESSE");

    private readonly CountingTimeProvider _time = new();
    private readonly Dictionary<FkJump, TaskCompletionSource<DbResult<long?>>> _answers = [];
    private readonly List<FkJump> _asked = [];
    private int _running;
    private int _mostAtOnce;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private FkCounts Create() => new(async (jump, token) =>
    {
        TaskCompletionSource<DbResult<long?>> answer;
        lock (_asked)
        {
            _asked.Add(jump);
            _mostAtOnce = Math.Max(_mostAtOnce, ++_running);
            answer = _answers[jump] = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        try
        {
            using (token.Register(() => answer.TrySetResult(new(default, null, Cancelled: true))))
            {
                return await answer.Task;
            }
        }
        finally
        {
            lock (_asked)
            {
                _running--;
            }
        }
    }, () => { }, _time);

    private IReadOnlyList<FkJump> Asked
    {
        get
        {
            lock (_asked)
            {
                return [.. _asked];
            }
        }
    }

    private void Answer(FkJump jump, long? rows)
    {
        lock (_asked)
        {
            _answers[jump].SetResult(new(rows, null, false));
        }
    }

    private static async Task Eventually(Func<bool> condition)
    {
        for (var waited = 0; !condition(); waited += 10)
        {
            Assert.True(waited < 5_000, "Timed out waiting for the counts.");
            await Task.Delay(10, Ct);
        }
    }

    [Fact]
    public async Task Immediately_counts_every_jump_one_after_the_other()
    {
        var counts = Create();

        var all = counts.CountAllAsync([Auftrag, Rechnung], Ct);
        await Eventually(() => Asked.Count == 1);
        Assert.IsType<FkCount.Loading>(counts.Of(Rechnung));
        Answer(Auftrag, 3);
        await Eventually(() => Asked.Count == 2);
        Answer(Rechnung, null);
        await all;

        Assert.Equal(new FkCount.Done(3), counts.Of(Auftrag));
        Assert.IsType<FkCount.TimedOut>(counts.Of(Rechnung));
        Assert.Equal(1, _mostAtOnce);
    }

    [Fact]
    public async Task Cancelling_stops_the_rest_and_leaves_nothing_loading()
    {
        var counts = Create();
        using var cts = new CancellationTokenSource();

        var all = counts.CountAllAsync([Auftrag, Rechnung], cts.Token);
        await Eventually(() => Asked.Count == 1);
        await cts.CancelAsync();
        await all;

        Assert.Equal([Auftrag], Asked);
        Assert.Null(counts.Of(Auftrag));
        Assert.Null(counts.Of(Rechnung));
    }

    [Fact]
    public async Task On_hover_the_entry_is_counted_once_the_mouse_rested_on_it()
    {
        var counts = Create();

        var hover = counts.HoverAsync(Auftrag, Ct);
        _time.Advance(FkCounts.HoverDelay / 2);
        await Eventually(() => _time.Timers == 1); // parked on the delay (review 3.40.0, C8: was a fixed wait of 50 ms)
        Assert.Empty(Asked);

        _time.Advance(FkCounts.HoverDelay);
        await Eventually(() => Asked.Count == 1);
        Assert.IsType<FkCount.Loading>(counts.Of(Auftrag));
        Answer(Auftrag, 7);
        await hover;

        Assert.Equal(new FkCount.Done(7), counts.Of(Auftrag));
        Assert.Null(counts.Of(Rechnung));
    }

    [Fact]
    public async Task Passing_over_entries_counts_nothing()
    {
        var counts = Create();

        var hover = counts.HoverAsync(Auftrag, Ct);
        counts.Leave(Auftrag);
        _time.Advance(FkCounts.HoverDelay);
        await hover;

        Assert.Empty(Asked);
    }

    [Fact]
    public async Task An_entry_hovered_while_a_count_runs_is_counted_after_it_one_at_a_time()
    {
        var counts = Create();
        var first = counts.HoverAsync(Auftrag, Ct);
        _time.Advance(FkCounts.HoverDelay);
        await Eventually(() => Asked.Count == 1);

        // Over Rechnung, then on to Adresse while Auftrag is still counted: only the entry under the mouse follows.
        counts.Leave(Auftrag);
        var second = counts.HoverAsync(Rechnung, Ct);
        counts.Leave(Rechnung);
        var third = counts.HoverAsync(Adresse, Ct);
        _time.Advance(FkCounts.HoverDelay);
        await Task.WhenAll(second, third);
        Assert.Single(Asked);

        Answer(Auftrag, 1);
        await Eventually(() => Asked.Count == 2);
        Answer(Adresse, 2);
        await first;

        Assert.Equal([Auftrag, Adresse], Asked);
        Assert.Null(counts.Of(Rechnung));
        Assert.Equal(1, _mostAtOnce);
    }

    [Fact]
    public async Task A_counted_entry_is_not_counted_again()
    {
        var counts = Create();
        var hover = counts.HoverAsync(Auftrag, Ct);
        _time.Advance(FkCounts.HoverDelay);
        await Eventually(() => Asked.Count == 1);
        Answer(Auftrag, 5);
        await hover;

        var again = counts.HoverAsync(Auftrag, Ct);
        _time.Advance(FkCounts.HoverDelay);
        await again;

        Assert.Single(Asked);
        Assert.Equal(new FkCount.Done(5), counts.Of(Auftrag));
    }

    [Fact]
    public async Task Reset_forgets_the_counts_of_the_previous_row()
    {
        var counts = Create();
        var all = counts.CountAllAsync([Auftrag], Ct);
        await Eventually(() => Asked.Count == 1);
        Answer(Auftrag, 5);
        await all;

        counts.Reset();

        Assert.Null(counts.Of(Auftrag));
    }
}
