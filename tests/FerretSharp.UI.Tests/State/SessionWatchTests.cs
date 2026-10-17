using System.Threading.Channels;
using FerretSharp.Core.Data;
using FerretSharp.UI.State;
using Microsoft.Extensions.Time.Testing;

namespace FerretSharp.UI.Tests.State;

/// <summary>
/// WP-52: the looks at a running statement's session start after a few seconds, follow each other only after an answer (no
/// pile-up over a slow network), and stop for good without rights or when the statement ends.
/// </summary>
public sealed class SessionWatchTests
{
    private readonly FakeTimeProvider _time = new();
    private readonly Channel<TaskCompletionSource<SessionActivity?>> _looks = Channel.CreateUnbounded<TaskCompletionSource<SessionActivity?>>();
    private int _changed;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private SessionWatch Create() => new(async token =>
    {
        var answer = new TaskCompletionSource<SessionActivity?>(TaskCreationOptions.RunContinuationsAsynchronously);
        await _looks.Writer.WriteAsync(answer, token);
        using (token.Register(() => answer.TrySetCanceled(token)))
        {
            return await answer.Task;
        }
    }, () => Interlocked.Increment(ref _changed), _time);

    private async Task<TaskCompletionSource<SessionActivity?>> NextLookAsync()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        return await _looks.Reader.ReadAsync(timeout.Token);
    }

    /// <summary>No look yet (the continuation after a timer runs on the pool: give it a moment to show up if it were there).</summary>
    private async Task AssertNoLookAsync()
    {
        await Task.Delay(50, Ct);
        Assert.False(_looks.Reader.TryRead(out _));
    }

    [Fact]
    public async Task Looks_start_after_the_delay_and_the_next_one_waits_for_the_answer()
    {
        using var watch = Create();
        var run = watch.RunAsync();

        _time.Advance(SessionWatch.Delay - TimeSpan.FromMilliseconds(100));
        await AssertNoLookAsync();
        _time.Advance(TimeSpan.FromMilliseconds(100));
        var first = await NextLookAsync();

        _time.Advance(TimeSpan.FromSeconds(30)); // the database is slow to answer: no second look meanwhile
        await AssertNoLookAsync();
        var waiting = new SessionActivity(new SessionWait("enq: TX - row lock contention", TimeSpan.FromSeconds(4), 77, null, null), null);
        first.SetResult(waiting);
        await WaitUntilAsync(() => watch.Latest is not null);
        Assert.Same(waiting, watch.Latest);
        Assert.False(watch.Unavailable);

        _time.Advance(SessionWatch.Interval);
        var second = await NextLookAsync();
        second.SetResult(SessionActivity.None);
        await WaitUntilAsync(() => watch.Latest == SessionActivity.None);

        watch.Dispose();
        await run.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.True(_changed >= 2);
    }

    [Fact]
    public async Task Without_rights_it_stops_and_says_so()
    {
        using var watch = Create();
        var run = watch.RunAsync();
        _time.Advance(SessionWatch.Delay);

        (await NextLookAsync()).SetResult(null);
        await run.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        Assert.True(watch.Unavailable);
        Assert.Null(watch.Latest);
        _time.Advance(TimeSpan.FromMinutes(1));
        await AssertNoLookAsync();
    }

    [Fact]
    public async Task A_statement_that_ends_early_costs_no_look()
    {
        var watch = Create();
        var run = watch.RunAsync();
        _time.Advance(TimeSpan.FromSeconds(1));

        watch.Dispose();
        await run.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        _time.Advance(TimeSpan.FromMinutes(1));

        await AssertNoLookAsync();
        Assert.Equal(0, _changed);
    }

    [Fact]
    public async Task Ending_while_a_look_runs_cancels_it()
    {
        var watch = Create();
        var run = watch.RunAsync();
        _time.Advance(SessionWatch.Delay);
        var look = await NextLookAsync();

        watch.Dispose();
        await run.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        Assert.True(look.Task.IsCanceled);
        Assert.False(watch.Unavailable);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < until, "timed out");
            await Task.Delay(10, Ct);
        }
    }
}
