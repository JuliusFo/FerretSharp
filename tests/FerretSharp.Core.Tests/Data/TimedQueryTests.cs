using FerretSharp.Core.Data;
using Microsoft.Extensions.Time.Testing;

namespace FerretSharp.Core.Tests.Data;

/// <summary>R5b (review 3.31.0, B7): the time limit of FK counts and display name lookups, once.</summary>
public sealed class TimedQueryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeTimeProvider _time = new();

    [Fact]
    public async Task A_query_in_time_returns_its_result()
    {
        Assert.Equal(42L, await TimedQuery.RunAsync<long?>(_ => Task.FromResult<long?>(42), TimeSpan.FromSeconds(5), Ct, _time));
    }

    [Fact]
    public async Task A_slow_query_returns_null_at_the_timeout_and_is_cancelled_on_the_server()
    {
        var server = new TaskCompletionSource<long?>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken statement = default;

        var result = TimedQuery.RunAsync(token =>
        {
            statement = token;
            token.Register(() => server.TrySetCanceled(token));
            return server.Task;
        }, TimeSpan.FromSeconds(5), Ct, _time);
        _time.Advance(TimeSpan.FromSeconds(5));

        Assert.Null(await result);
        Assert.True(statement.IsCancellationRequested);
    }

    [Fact]
    public async Task Cancelling_throws_as_usual()
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var server = new TaskCompletionSource<string>();

        var result = TimedQuery.RunAsync(_ => server.Task, TimeSpan.FromSeconds(5), cancel.Token, _time);
        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => result);
    }

    [Fact]
    public async Task A_query_failing_after_the_timeout_is_observed()
    {
        var unobserved = false;
        void OnUnobserved(object? sender, UnobservedTaskExceptionEventArgs e) => unobserved |= e.Exception.InnerException is InvalidOperationException { Message: "connection lost" };
        TaskScheduler.UnobservedTaskException += OnUnobserved;
        try
        {
            await RunAndFailLateAsync();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            Assert.False(unobserved);
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= OnUnobserved;
        }
    }

    private async Task RunAndFailLateAsync()
    {
        var server = new TaskCompletionSource<string>();
        var result = TimedQuery.RunAsync(_ => server.Task, TimeSpan.FromSeconds(5), Ct, _time);
        _time.Advance(TimeSpan.FromSeconds(5));
        Assert.Null(await result);
        server.SetException(new InvalidOperationException("connection lost"));
    }
}
