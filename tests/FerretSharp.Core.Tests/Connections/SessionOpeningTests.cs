using FerretSharp.Core.Connections;
using FerretSharp.Testing;
using NSubstitute;

namespace FerretSharp.Core.Tests.Connections;

public sealed class SessionOpeningTests
{
    private readonly CountingTimeProvider _time = new();
    private readonly List<TaskCompletionSource<IDatabaseConnection>> _attempts = [];
    private readonly List<int> _retrying = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// One attempt each call, open until the test ends it; ending it runs the race's bookkeeping right away (no
    /// <see cref="TaskCreationOptions.RunContinuationsAsynchronously"/>), as a driver's thread would.
    /// </summary>
    private Task<IDatabaseConnection> Attempt(int number, CancellationToken cancellationToken)
    {
        var attempt = new TaskCompletionSource<IDatabaseConnection>();
        lock (_attempts)
        {
            Assert.Equal(_attempts.Count + 1, number);
            _attempts.Add(attempt);
        }

        return attempt.Task;
    }

    private TaskCompletionSource<IDatabaseConnection> AttemptNo(int number)
    {
        lock (_attempts)
        {
            return _attempts[number - 1];
        }
    }

    private int Attempts
    {
        get
        {
            lock (_attempts)
            {
                return _attempts.Count;
            }
        }
    }

    private Task<IDatabaseConnection> OpenAsync(CancellationToken cancellationToken) =>
        SessionOpening.OpenAsync(Attempt, _time, _retrying.Add, cancellationToken);

    /// <summary>Waits until attempt <paramref name="number"/> was started and waits on its limit, then lets the limit run.</summary>
    private async Task RunPastLimitAsync(int number)
    {
        await Wait.UntilAsync(() => Attempts == number && _time.Timers == number);
        _time.Advance(SessionOpening.AttemptLimits[number - 1]);
    }

    /// <summary>The VPN case (2026-10-28): the first attempt hangs for 20 s, the next one opens at once.</summary>
    [Fact]
    public async Task Past_its_limit_the_next_attempt_starts_and_a_session_opening_late_is_closed()
    {
        var opening = OpenAsync(Ct);

        await RunPastLimitAsync(1);
        await Wait.UntilAsync(() => Attempts == 2);
        var session = FakeDatabase.Connection();
        AttemptNo(2).SetResult(session);

        Assert.Same(session, await opening.WaitAsync(TimeSpan.FromSeconds(5), Ct));
        Assert.Equal([2], _retrying);
        var late = FakeDatabase.Connection();
        AttemptNo(1).SetResult(late);
        await Wait.UntilAsync(() => Disposed(late));
        Assert.False(Disposed(session));
    }

    /// <summary>Review of the fix (2): a slow but healthy line opened just past the limit and the session was thrown away.</summary>
    [Fact]
    public async Task An_attempt_past_its_limit_keeps_running_and_wins_if_it_opens_first()
    {
        var opening = OpenAsync(Ct);
        await RunPastLimitAsync(1);
        await Wait.UntilAsync(() => Attempts == 2);

        var slow = FakeDatabase.Connection();
        AttemptNo(1).SetResult(slow);

        Assert.Same(slow, await opening.WaitAsync(TimeSpan.FromSeconds(5), Ct));
        var second = FakeDatabase.Connection();
        AttemptNo(2).SetResult(second);
        await Wait.UntilAsync(() => Disposed(second));
        Assert.False(Disposed(slow));
    }

    [Fact]
    public async Task The_limits_start_at_5_s_and_grow()
    {
        Assert.Equal(
            [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(15)],
            SessionOpening.AttemptLimits);

        // Each attempt waits with its own limit, not the first one's (second review: a check by timing passed either way).
        var opening = OpenAsync(Ct);
        await RunPastLimitAsync(1);
        await RunPastLimitAsync(2);
        await Wait.UntilAsync(() => Attempts == 3 && _time.Timers == 3);

        Assert.Equal(SessionOpening.AttemptLimits.Take(3), _time.DueTimes);
        AttemptNo(3).SetResult(FakeDatabase.Connection());
        await opening.WaitAsync(TimeSpan.FromSeconds(5), Ct);
    }

    /// <summary>
    /// Second review (B): the latest attempt failing on the network while an earlier one still runs does not end the open – the
    /// slow earlier one may still open. Once none runs any more, the latest attempt's error ends it.
    /// </summary>
    [Fact]
    public async Task A_network_error_of_the_latest_attempt_waits_for_the_earlier_ones()
    {
        var opening = OpenAsync(Ct);
        await RunPastLimitAsync(1);
        await Wait.UntilAsync(() => Attempts == 2);

        AttemptNo(2).SetException(new DatabaseException("no listener", "ORA-12541"));
        Assert.False(opening.IsCompleted);
        var slow = FakeDatabase.Connection();
        AttemptNo(1).SetResult(slow);

        Assert.Same(slow, await opening.WaitAsync(TimeSpan.FromSeconds(5), Ct));
    }

    /// <summary>
    /// Third review (D): after the latest attempt failed, its limit running must not start another one ("attempt 3 of 5" while
    /// attempt 1 still hung) – only the earlier ones are waited for, as long as the remaining limits would have taken.
    /// </summary>
    [Fact]
    public async Task After_the_latest_attempt_failed_no_further_one_starts()
    {
        var opening = OpenAsync(Ct);
        await RunPastLimitAsync(1);
        await Wait.UntilAsync(() => Attempts == 2 && _time.Timers == 2);
        AttemptNo(2).SetException(new DatabaseException("no listener", "ORA-12541"));

        _time.Advance(SessionOpening.AttemptLimits[1]);
        await Wait.UntilAsync(() => _time.Timers == 3); // waiting for the earlier attempt only

        Assert.Equal(2, Attempts);
        Assert.Equal([2], _retrying);
        Assert.Equal(SessionOpening.AttemptLimits.Skip(2).Aggregate(TimeSpan.Zero, (sum, l) => sum + l), _time.DueTimes[2]);
        var slow = FakeDatabase.Connection();
        AttemptNo(1).SetResult(slow);
        Assert.Same(slow, await opening.WaitAsync(TimeSpan.FromSeconds(5), Ct));
    }

    [Fact]
    public async Task After_the_latest_attempt_failed_the_open_times_out_when_the_earlier_ones_hang_on()
    {
        var opening = OpenAsync(Ct);
        await RunPastLimitAsync(1);
        await Wait.UntilAsync(() => Attempts == 2 && _time.Timers == 2);
        AttemptNo(2).SetException(new DatabaseException("no listener", "ORA-12541"));
        _time.Advance(SessionOpening.AttemptLimits[1]);
        await Wait.UntilAsync(() => _time.Timers == 3);

        _time.Advance(SessionOpening.AttemptLimits.Skip(2).Aggregate(TimeSpan.Zero, (sum, l) => sum + l));

        var error = await Assert.ThrowsAsync<SessionOpenTimeoutException>(() => opening.WaitAsync(TimeSpan.FromSeconds(5), Ct));
        Assert.EndsWith("Zuletzt: no listener", error.Message);
        Assert.Equal(2, Attempts);
    }

    [Fact]
    public async Task A_network_error_of_the_latest_attempt_ends_the_open_once_none_runs_any_more()
    {
        var opening = OpenAsync(Ct);
        await RunPastLimitAsync(1);
        await Wait.UntilAsync(() => Attempts == 2);

        AttemptNo(2).SetException(new DatabaseException("no listener", "ORA-12541"));
        AttemptNo(1).SetException(new DatabaseException("packet", "ORA-12570"));

        var error = await Assert.ThrowsAsync<DatabaseException>(() => opening.WaitAsync(TimeSpan.FromSeconds(5), Ct));
        Assert.Equal("ORA-12541", error.ErrorCode);
        Assert.Equal(2, Attempts);
    }

    /// <summary>
    /// Second review (A): a session that won just as the open ended without taking it (the limit ran or it was cancelled in
    /// the same moment) was left open – with <c>Pooling=false</c> a real Oracle session until the app ends.
    /// </summary>
    [Fact]
    public void A_session_winning_as_the_open_ends_without_it_is_closed()
    {
        var race = new SessionOpening.Race();
        var opening = new TaskCompletionSource<IDatabaseConnection>();
        race.Start(1, opening.Task);
        var session = FakeDatabase.Connection();
        opening.SetResult(session);

        race.End(taken: false);

        Assert.True(Disposed(session));
    }

    [Fact]
    public void A_session_the_open_returned_is_not_closed_at_its_end()
    {
        var race = new SessionOpening.Race();
        var opening = new TaskCompletionSource<IDatabaseConnection>();
        race.Start(1, opening.Task);
        var session = FakeDatabase.Connection();
        opening.SetResult(session);

        race.End(taken: true);

        Assert.False(Disposed(session));
    }

    [Fact]
    public async Task When_no_attempt_opens_within_the_limits_the_open_fails_as_timed_out()
    {
        var opening = OpenAsync(Ct);
        for (var attempt = 1; attempt <= SessionOpening.AttemptLimits.Count; attempt++)
        {
            await RunPastLimitAsync(attempt);
        }

        var error = await Assert.ThrowsAsync<SessionOpenTimeoutException>(() => opening.WaitAsync(TimeSpan.FromSeconds(5), Ct));

        Assert.Equal([2, 3, 4, 5], _retrying);
        Assert.Null(error.ErrorCode);
        using (UiCulture.Use("en"))
        {
            Assert.Equal("Opening the session did not finish in 5 attempts (43 s in all).",
                new SessionOpenTimeoutException(SessionOpening.AttemptLimits, null).Message);
        }
    }

    /// <summary>
    /// Review of the fix (5): with the VPN down, ODP.NET's own error came after 15 s; now it comes from an attempt given up
    /// on and must not get lost. It does not end the open either: it is the hang ending, the next attempt may still open.
    /// </summary>
    [Fact]
    public async Task A_late_network_error_keeps_the_open_going_and_is_named_if_none_opens()
    {
        var opening = OpenAsync(Ct);
        await RunPastLimitAsync(1);
        await Wait.UntilAsync(() => Attempts == 2);
        AttemptNo(1).SetException(new DatabaseException("ORA-12570: Netzwerksession: Unerwarteter Fehler beim Lesen des Pakets", "ORA-12570"));
        for (var attempt = 2; attempt <= SessionOpening.AttemptLimits.Count; attempt++)
        {
            await RunPastLimitAsync(attempt);
        }

        var error = await Assert.ThrowsAsync<SessionOpenTimeoutException>(() => opening.WaitAsync(TimeSpan.FromSeconds(5), Ct));

        Assert.Equal(5, Attempts);
        Assert.EndsWith("Zuletzt: ORA-12570: Netzwerksession: Unerwarteter Fehler beim Lesen des Pakets", error.Message);
    }

    /// <summary>
    /// Review of the fix (1): Oracle answers logins late after failed ones; a refused login past the limit was tried again with
    /// the same password – each one counts towards <c>FAILED_LOGIN_ATTEMPTS</c>.
    /// </summary>
    [Fact]
    public async Task A_refused_login_answered_past_the_limit_ends_the_open()
    {
        var opening = OpenAsync(Ct);
        await RunPastLimitAsync(1);
        await Wait.UntilAsync(() => Attempts == 2);

        AttemptNo(1).SetException(new DatabaseException("invalid username/password", "ORA-01017"));

        var error = await Assert.ThrowsAsync<DatabaseException>(() => opening.WaitAsync(TimeSpan.FromSeconds(5), Ct));
        Assert.Equal("ORA-01017", error.ErrorCode);
        _time.Advance(SessionOpening.AttemptLimits.Aggregate(TimeSpan.Zero, (sum, l) => sum + l));
        Assert.Equal(2, Attempts);
        var late = FakeDatabase.Connection();
        AttemptNo(2).SetResult(late);
        await Wait.UntilAsync(() => Disposed(late));
    }

    /// <summary>A refused login must not be tried again; neither a listener that is not there (it would not be there 1 s later).</summary>
    [Theory]
    [InlineData("ORA-01017")]
    [InlineData("ORA-12541")]
    public async Task A_failure_within_the_limit_is_not_tried_again(string code)
    {
        var opening = OpenAsync(Ct);
        await Wait.UntilAsync(() => Attempts == 1);

        AttemptNo(1).SetException(new DatabaseException("refused", code));

        var error = await Assert.ThrowsAsync<DatabaseException>(() => opening.WaitAsync(TimeSpan.FromSeconds(5), Ct));
        Assert.Equal(code, error.ErrorCode);
        _time.Advance(SessionOpening.AttemptLimits.Aggregate(TimeSpan.Zero, (sum, l) => sum + l));
        Assert.Equal(1, Attempts);
        Assert.Empty(_retrying);
    }

    [Fact]
    public async Task Cancelling_ends_the_open_without_another_attempt_and_closes_a_late_session()
    {
        using var cts = new CancellationTokenSource();
        var opening = OpenAsync(cts.Token);
        await RunPastLimitAsync(1);
        await Wait.UntilAsync(() => Attempts == 2);

        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => opening.WaitAsync(TimeSpan.FromSeconds(5), Ct));
        _time.Advance(SessionOpening.AttemptLimits.Aggregate(TimeSpan.Zero, (sum, l) => sum + l));
        Assert.Equal(2, Attempts);
        var late = FakeDatabase.Connection();
        AttemptNo(1).SetResult(late);
        await Wait.UntilAsync(() => Disposed(late));
    }

    /// <summary>Review of the fix (6): a driver that blocks before returning its task must not hold up the limit.</summary>
    [Fact]
    public async Task An_attempt_blocking_its_thread_does_not_hold_up_the_limit()
    {
        using var release = new ManualResetEventSlim();
        try
        {
            var session = FakeDatabase.Connection();
            // On the thread pool, so that a regression fails the test instead of blocking it (second review).
            var opening = Task.Run(() => SessionOpening.OpenAsync((number, _) =>
            {
                if (number == 1)
                {
                    release.Wait(TimeSpan.FromSeconds(10)); // like a synchronous connect inside OpenAsync
                    return Task.FromResult(FakeDatabase.Connection());
                }

                return Task.FromResult(session);
            }, _time, null, Ct), Ct);

            await Wait.UntilAsync(() => _time.Timers == 1);
            _time.Advance(SessionOpening.AttemptLimits[0]);

            Assert.Same(session, await opening.WaitAsync(TimeSpan.FromSeconds(5), Ct));
        }
        finally
        {
            release.Set();
        }
    }

    private static bool Disposed(IDatabaseConnection connection) =>
        connection.ReceivedCalls().Any(c => c.GetMethodInfo().Name == nameof(IAsyncDisposable.DisposeAsync));
}
