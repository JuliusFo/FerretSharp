using FerretSharp.Core.Oracle;

namespace FerretSharp.Core.Tests.Oracle;

/// <summary>
/// The session gate without a database (R3b): until then the trickiest part of the session – one caller at a time,
/// closing that lets everyone go, work that ignores its cancel – could only be reproduced by hand with a TCP proxy.
/// </summary>
public sealed class SessionGateTests
{
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(200);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static SessionGate Gate(Func<Exception, Exception?>? translate = null, Action? released = null) =>
        new(translate ?? (_ => null), released);

    [Fact]
    public async Task One_caller_at_a_time()
    {
        var gate = Gate();
        var first = new TaskCompletionSource<int>();
        var running = gate.RunAsync(() => first.Task, Ct);
        var secondStarted = false;

        var second = gate.RunAsync(() =>
        {
            secondStarted = true;
            return Task.FromResult(2);
        }, Ct);
        await Task.Delay(Short, Ct);

        Assert.False(secondStarted);
        Assert.True(gate.IsBusy);

        first.SetResult(1);
        Assert.Equal(1, await running);
        Assert.Equal(2, await second);
        Assert.False(gate.IsBusy);
    }

    [Fact]
    public async Task Errors_of_the_work_are_translated()
    {
        var gate = Gate(ex => ex is TimeoutException ? new InvalidDataException("translated", ex) : null);

        var translated = await Assert.ThrowsAsync<InvalidDataException>(() => gate.RunAsync<int>(() => throw new TimeoutException(), Ct));
        await Assert.ThrowsAsync<FormatException>(() => gate.RunAsync<int>(() => throw new FormatException(), Ct));

        Assert.IsType<TimeoutException>(translated.InnerException);
        Assert.False(gate.IsBusy);
    }

    [Fact]
    public async Task Closing_lets_waiting_callers_go_at_once()
    {
        var gate = Gate();
        var hold = new TaskCompletionSource<int>();
        var running = gate.RunAsync(() => hold.Task, Ct);
        var waiting = gate.RunAsync(() => Task.FromResult(2), Ct);

        await gate.BeginCloseAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gate.RunAsync(() => Task.FromResult(3), Ct));
        hold.SetResult(1);
    }

    /// <summary>
    /// Work that ignores the cancel (a command on a VPN that went silent): its caller is let go, but the work keeps the gate
    /// until it really ends – closing cannot take it meanwhile – and releases it then.
    /// </summary>
    [Fact]
    public async Task Work_that_ignores_the_cancel_keeps_the_gate_until_it_ends()
    {
        var releases = 0;
        var gate = Gate(released: () => Interlocked.Increment(ref releases));
        var hangs = new TaskCompletionSource<int>();
        var caller = gate.RunAsync(() => hangs.Task, CancellationToken.None);

        await gate.BeginCloseAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => caller.WaitAsync(TimeSpan.FromSeconds(2), Ct));
        Assert.False(await gate.EnterAfterCloseAsync(Short));
        Assert.True(gate.IsBusy);

        hangs.SetException(new IOException("the connection finally gave up")); // observed, not thrown anywhere
        Assert.True(await gate.EnterAfterCloseAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(1, releases);
        gate.Release();
    }

    [Fact]
    public async Task An_error_while_closing_is_the_closing_not_a_failure()
    {
        var gate = Gate(ex => new InvalidDataException("would look like a lost connection", ex));
        var started = new TaskCompletionSource();
        var caller = gate.RunAsync<int>(async () =>
        {
            started.SetResult();
            await Task.Delay(Short, CancellationToken.None);
            throw new IOException("connection closed under the command");
        }, Ct);
        await started.Task;

        await gate.BeginCloseAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => caller);
    }

    [Fact]
    public async Task Cancelling_one_caller_while_waiting_leaves_the_gate_usable()
    {
        var gate = Gate();
        var hold = new TaskCompletionSource<int>();
        var running = gate.RunAsync(() => hold.Task, Ct);
        using var cancel = new CancellationTokenSource();
        var waiting = gate.RunAsync(() => Task.FromResult(2), cancel.Token);

        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        hold.SetResult(1);
        Assert.Equal(1, await running);
        Assert.Equal(3, await gate.RunAsync(() => Task.FromResult(3), Ct));
    }
}
