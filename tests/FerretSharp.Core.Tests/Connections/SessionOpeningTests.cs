using FerretSharp.Core.Connections;
using FerretSharp.Testing;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace FerretSharp.Core.Tests.Connections;

public sealed class SessionOpeningTests
{
    private readonly FakeTimeProvider _time = new();
    private readonly List<TaskCompletionSource<IDatabaseConnection>> _attempts = [];
    private readonly List<int> _retrying = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>One attempt each call, open until the test ends it; counted, so the test can wait until its limit runs.</summary>
    private Task<IDatabaseConnection> Attempt(CancellationToken cancellationToken)
    {
        var attempt = new TaskCompletionSource<IDatabaseConnection>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_attempts)
        {
            _attempts.Add(attempt);
        }

        return attempt.Task;
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

    /// <summary>The VPN case (2026-10-28): the first attempt hangs for 20 s, the next one opens at once.</summary>
    [Fact]
    public async Task An_attempt_past_its_limit_makes_way_for_the_next_and_its_late_session_is_closed()
    {
        var opening = OpenAsync(Ct);
        await Eventually(() => Attempts == 1);

        _time.Advance(SessionOpening.AttemptLimits[0]);
        await Eventually(() => Attempts == 2);
        var session = FakeDatabase.Connection();
        _attempts[1].SetResult(session);

        Assert.Same(session, await opening.WaitAsync(TimeSpan.FromSeconds(5), Ct));
        Assert.Equal([2], _retrying);
        var late = FakeDatabase.Connection();
        _attempts[0].SetResult(late);
        await Eventually(() => late.ReceivedCalls().Any(c => c.GetMethodInfo().Name == nameof(IAsyncDisposable.DisposeAsync)));
        Assert.DoesNotContain(session.ReceivedCalls(), c => c.GetMethodInfo().Name == nameof(IAsyncDisposable.DisposeAsync));
    }

    /// <summary>The limits grow, so a slow but healthy line gets through in a later attempt (2.4 s seen on Prod).</summary>
    [Fact]
    public async Task The_limits_grow()
    {
        Assert.Equal(5, SessionOpening.AttemptLimits.Count);
        Assert.Equal(TimeSpan.FromSeconds(3), SessionOpening.AttemptLimits[0]);
        Assert.Equal(TimeSpan.FromSeconds(15), SessionOpening.AttemptLimits[^1]);
        Assert.Equal(SessionOpening.AttemptLimits.Order(), SessionOpening.AttemptLimits);

        // An attempt is given up only when its own limit has run, not the first one's.
        var opening = OpenAsync(Ct);
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            await Eventually(() => Attempts == attempt);
            _time.Advance(SessionOpening.AttemptLimits[attempt - 1]);
        }

        await Eventually(() => Attempts == 3);
        _time.Advance(SessionOpening.AttemptLimits[1]);
        _attempts[2].SetResult(FakeDatabase.Connection());

        await opening.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        Assert.Equal(3, Attempts);
    }

    [Fact]
    public async Task When_every_attempt_runs_past_its_limit_the_open_fails_as_timed_out()
    {
        var opening = OpenAsync(Ct);
        for (var attempt = 1; attempt <= SessionOpening.AttemptLimits.Count; attempt++)
        {
            await Eventually(() => Attempts == attempt);
            _time.Advance(SessionOpening.AttemptLimits[attempt - 1]);
        }

        var error = await Assert.ThrowsAsync<SessionOpenTimeoutException>(() => opening.WaitAsync(TimeSpan.FromSeconds(5), Ct));

        Assert.Equal([2, 3, 4, 5], _retrying);
        Assert.Null(error.ErrorCode);
        using (UiCulture.Use("en"))
        {
            Assert.Equal("Opening the session did not finish in 5 attempts (34 s in all).", new SessionOpenTimeoutException(SessionOpening.AttemptLimits).Message);
        }
    }

    /// <summary>A refused login must not be tried again: each one counts towards <c>FAILED_LOGIN_ATTEMPTS</c>.</summary>
    [Theory]
    [InlineData("ORA-01017")]
    [InlineData("ORA-12541")]
    public async Task A_failure_within_the_limit_is_not_tried_again(string code)
    {
        var opening = OpenAsync(Ct);
        await Eventually(() => Attempts == 1);

        _attempts[0].SetException(new DatabaseException("refused", code));

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
        await Eventually(() => Attempts == 1);

        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => opening.WaitAsync(TimeSpan.FromSeconds(5), Ct));
        _time.Advance(SessionOpening.AttemptLimits[0]);
        Assert.Equal(1, Attempts);
        var late = FakeDatabase.Connection();
        _attempts[0].SetResult(late);
        await Eventually(() => late.ReceivedCalls().Any(c => c.GetMethodInfo().Name == nameof(IAsyncDisposable.DisposeAsync)));
    }

    private static async Task Eventually(Func<bool> condition)
    {
        for (var waited = 0; !condition(); waited += 10)
        {
            Assert.True(waited < 5_000, "Timed out.");
            await Task.Delay(10, Ct);
        }
    }
}
