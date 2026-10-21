using FerretSharp.Core.Connections;
using FerretSharp.UI.State;
using Microsoft.Extensions.Logging.Abstractions;

namespace FerretSharp.UI.Tests.State;

/// <summary>
/// A lost session belongs to its connection (WP-24: several are open). Before R3a the shell kept one global "lost" flag:
/// a background connection's loss showed the banner on the healthy one, and "Neu verbinden" stopped every later question
/// about uncommitted work.
/// </summary>
public sealed class ConnectionLostTests : IAsyncDisposable
{
    private readonly TestApp _app = new();

    [Fact]
    public async Task Lost_session_of_a_background_connection_marks_only_that_connection()
    {
        var test = await _app.OpenAsync(TestApp.Profile("Test"));
        var dev = await _app.OpenAsync(TestApp.Profile("Dev"));

        var result = await _app.Shell.CallDbAsync<int>(NullLogger.Instance, test.Active, () => throw TestApp.LostError());

        Assert.NotNull(result.Error);
        Assert.NotNull(test.Lost);
        Assert.Null(dev.Lost);
        Assert.Same(dev, _app.Hub.Shown);
    }

    [Fact]
    public async Task Other_errors_do_not_count_as_lost()
    {
        var test = await _app.OpenAsync(TestApp.Profile("Test"));

        await _app.Shell.CallDbAsync<int>(NullLogger.Instance, test.Active, () => throw new DatabaseException("table or view does not exist", "ORA-00942"));

        Assert.Null(test.Lost);
    }

    [Fact]
    public async Task Leaving_still_asks_about_the_healthy_connection_while_another_is_lost()
    {
        var test = await _app.OpenAsync(TestApp.Profile("Test"));
        var dev = await _app.OpenAsync(TestApp.Profile("Dev"));
        _app.AddPendingWork(test);
        _app.AddPendingWork(dev);
        test.Active.ReportLost(TestApp.LostError());

        var exited = false;
        await _app.Lifecycle.RequestExitAsync(() => exited = true);

        Assert.False(exited);
        Assert.Equal([dev.Workspaces.Active!.Id], _app.Lifecycle.PendingLeave!.WorkspaceIds);
        Assert.False(_app.Lifecycle.CanExit);
    }

    [Fact]
    public async Task Work_of_a_lost_connection_does_not_block_exiting()
    {
        var test = await _app.OpenAsync(TestApp.Profile("Test"));
        await _app.OpenAsync(TestApp.Profile("Dev"));
        _app.AddPendingWork(test);

        Assert.False(_app.Lifecycle.CanExit);

        test.Active.ReportLost(TestApp.LostError());

        Assert.True(_app.Lifecycle.CanExit);
    }

    [Fact]
    public async Task Reconnecting_one_connection_keeps_asking_about_the_others()
    {
        var test = await _app.OpenAsync(TestApp.Profile("Test"));
        var dev = await _app.OpenAsync(TestApp.Profile("Dev"));
        _app.AddPendingWork(dev);
        test.Active.ReportLost(TestApp.LostError());

        _app.Lifecycle.Reconnect(test.Profile!);

        Assert.Null(test.Lost); // the banner goes away right away
        var closed = false;
        await _app.Lifecycle.GuardAsync("Workspace schließen", [_app.WorkspaceOf(dev)], () =>
        {
            closed = true;
            return Task.CompletedTask;
        });

        Assert.False(closed);
        Assert.NotNull(_app.Lifecycle.PendingLeave);
    }

    public ValueTask DisposeAsync() => _app.DisposeAsync();
}
