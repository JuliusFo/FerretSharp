using FerretSharp.Core.Connections;
using FerretSharp.Core.Resources;
using FerretSharp.UI.Resources;
using FerretSharp.UI.State;
using Microsoft.Extensions.Logging.Abstractions;

namespace FerretSharp.UI.Tests.State;

/// <summary>
/// Reconnecting by itself (decision of the user 2026-10-21): only without uncommitted work, where the user is, with pauses
/// while the network is away, never again after a refused login.
/// </summary>
public sealed class AutoReconnectTests : IAsyncDisposable
{
    private readonly TestApp _app = new();
    private readonly AutoReconnect _reconnect;
    private readonly Lock _lock = new();
    private readonly List<Task> _posted = [];

    public AutoReconnectTests() =>
        _reconnect = new AutoReconnect(_app.Shell, _app.Hub, _app.Editing, _app.Settings, _app.Lifecycle.ReconnectInPlaceAsync, Post,
            NullLogger<AutoReconnect>.Instance, _app.Time);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>What the shell's <c>Run</c> does, without a dispatcher: starts the work, the test waits for it (<see cref="SettleAsync"/>).</summary>
    private void Post(Func<Task> work)
    {
        var task = work();
        lock (_lock)
        {
            _posted.Add(task);
        }
    }

    /// <summary>Waits for everything posted, also what that posted in turn.</summary>
    private async Task SettleAsync()
    {
        while (true)
        {
            Task[] tasks;
            lock (_lock)
            {
                tasks = [.. _posted];
                _posted.Clear();
            }

            if (tasks.Length == 0)
            {
                return;
            }

            await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(10), Ct);
        }
    }

    private Task SwitchOnAsync() => _app.Settings.UpdateAsync(s => s with { AutoReconnect = true }, Ct);

    private async Task LoseAsync(ConnectionScope scope)
    {
        Assert.True(scope.Active.ReportLost(TestApp.LostError()));
        _reconnect.Check(scope); // the shell does on the connection's Changed
        await SettleAsync();
    }

    private async Task AdvanceAsync(TimeSpan by)
    {
        _app.Time.Advance(by);
        await SettleAsync();
    }

    private static DatabaseException NoListener() => new("TNS:no listener", "ORA-12541");

    [Fact]
    public async Task A_lost_connection_in_the_background_is_connected_again_where_the_user_is()
    {
        await SwitchOnAsync();
        var test = await _app.OpenAsync(TestApp.Profile("Test"));
        var dev = await _app.OpenAsync(TestApp.Profile("Dev"));
        _app.Shell.ShowSettings();

        await LoseAsync(test);

        Assert.True(test.Active.IsConnected);
        Assert.Null(test.Lost);
        Assert.Null(_reconnect.RunOf(test));
        Assert.Same(dev, _app.Hub.Current); // no switch …
        Assert.Equal(ShellPage.Settings, _app.Shell.Page); // … and no jump to the explorer
        Assert.Equal(TextFormat.Format(ShellText.AutoReconnect_Done, "Test"), _app.Shell.Notice?.Text);
    }

    [Fact]
    public async Task Uncommitted_work_keeps_the_banner()
    {
        await SwitchOnAsync();
        var test = await _app.OpenAsync(TestApp.Profile("Test"));
        _app.AddPendingWork(test);
        var attempts = _app.OpenAttempts;

        await LoseAsync(test);

        Assert.NotNull(test.Lost);
        Assert.Null(_reconnect.RunOf(test));
        Assert.True(_reconnect.KeptForWork(test));
        Assert.Equal(attempts, _app.OpenAttempts);
    }

    [Fact]
    public async Task Switched_off_nothing_happens()
    {
        var test = await _app.OpenAsync(TestApp.Profile("Test"));

        await LoseAsync(test);

        Assert.NotNull(test.Lost);
        Assert.Null(_reconnect.RunOf(test));
        Assert.False(_reconnect.KeptForWork(test));
    }

    [Fact]
    public async Task While_the_network_is_away_it_tries_again_after_a_pause()
    {
        await SwitchOnAsync();
        var test = await _app.OpenAsync(TestApp.Profile("Test"));
        _app.FailOpen = NoListener;

        await LoseAsync(test);

        var run = _reconnect.RunOf(test);
        Assert.Equal(ReconnectPhase.Waiting, run?.Phase);
        Assert.Equal(1, run!.Attempts);
        Assert.Equal(_app.Time.GetUtcNow() + ReconnectPolicy.Pauses[0], run.NextAt);
        Assert.Equal(ConnectionStatus.Failed, test.Active.Status);

        _app.FailOpen = null;
        await AdvanceAsync(ReconnectPolicy.Pauses[0] - TimeSpan.FromSeconds(1));
        Assert.Equal(ConnectionStatus.Failed, test.Active.Status);

        await AdvanceAsync(TimeSpan.FromSeconds(1));
        Assert.True(test.Active.IsConnected);
        Assert.Null(_reconnect.RunOf(test));
        Assert.Equal(TextFormat.Format(ShellText.AutoReconnect_Done, "Test"), _app.Shell.Notice?.Text);
    }

    [Fact]
    public async Task Gives_up_ten_minutes_after_the_loss()
    {
        await SwitchOnAsync();
        var test = await _app.OpenAsync(TestApp.Profile("Test"));
        _app.FailOpen = NoListener;
        var before = _app.OpenAttempts;

        await LoseAsync(test);
        while (_reconnect.RunOf(test) is { Phase: ReconnectPhase.Waiting, NextAt: { } next })
        {
            await AdvanceAsync(next - _app.Time.GetUtcNow());
        }

        // At 0, 5, 20 and 50 s, then every minute up to 9:50.
        Assert.Equal(13, _app.OpenAttempts - before);
        Assert.Null(_reconnect.RunOf(test));
        Assert.Equal(ConnectionStatus.Failed, test.Active.Status);
        Assert.Equal(TextFormat.Format(ShellText.AutoReconnect_GaveUp, "Test"), _app.Shell.Notice?.Text);
    }

    /// <summary>Another wrong password counts towards FAILED_LOGIN_ATTEMPTS and can lock the account.</summary>
    [Fact]
    public async Task A_refused_login_is_not_tried_again()
    {
        await SwitchOnAsync();
        var test = await _app.OpenAsync(TestApp.Profile("Test"));
        _app.FailOpen = () => new DatabaseException("invalid username/password; logon denied", ConnectionManager.WrongPasswordCode);
        var before = _app.OpenAttempts;

        await LoseAsync(test);
        await AdvanceAsync(ReconnectPolicy.GiveUpAfter);

        Assert.Equal(1, _app.OpenAttempts - before);
        Assert.Null(_reconnect.RunOf(test));
        Assert.Equal(ConnectionStatus.Failed, test.Active.Status);
        Assert.Equal(TextFormat.Format(ShellText.AutoReconnect_GaveUp, "Test"), _app.Shell.Notice?.Text);
        Assert.Equal(["invalid username/password; logon denied"], _app.Shell.Notice!.Warnings);
    }

    [Fact]
    public async Task Waits_while_a_write_holds_a_session()
    {
        await SwitchOnAsync();
        var test = await _app.OpenAsync(TestApp.Profile("Test"));
        var running = _app.Editing.StatementRunning(test.Workspaces.Active!.Id);
        var before = _app.OpenAttempts;

        await LoseAsync(test);
        Assert.Equal(ReconnectPhase.Holding, _reconnect.RunOf(test)?.Phase);
        await AdvanceAsync(AutoReconnect.HoldRecheck);
        Assert.Equal(before, _app.OpenAttempts);

        running.Dispose();
        await AdvanceAsync(AutoReconnect.HoldRecheck);

        Assert.True(test.Active.IsConnected);
        Assert.Null(_reconnect.RunOf(test));
    }

    [Fact]
    public async Task Reconnecting_by_hand_while_holding_ends_the_run()
    {
        await SwitchOnAsync();
        var test = await _app.OpenAsync(TestApp.Profile("Test"));
        using var running = _app.Editing.StatementRunning(test.Workspaces.Active!.Id);
        await LoseAsync(test);

        _app.Lifecycle.Reconnect(test.Profile!);
        _reconnect.Check(test);

        Assert.Null(_reconnect.RunOf(test));
    }

    [Fact]
    public async Task Stop_ends_the_attempts_and_the_connection_stays_failed()
    {
        await SwitchOnAsync();
        var test = await _app.OpenAsync(TestApp.Profile("Test"));
        _app.FailOpen = NoListener;
        await LoseAsync(test);
        var attempts = _app.OpenAttempts;

        _reconnect.Stop(test);
        await AdvanceAsync(ReconnectPolicy.GiveUpAfter);

        Assert.Equal(attempts, _app.OpenAttempts);
        Assert.Null(_reconnect.RunOf(test));
        Assert.Equal(ConnectionStatus.Failed, test.Active.Status);
    }

    [Fact]
    public async Task Try_now_does_not_wait_for_the_pause()
    {
        await SwitchOnAsync();
        var test = await _app.OpenAsync(TestApp.Profile("Test"));
        _app.FailOpen = NoListener;
        await LoseAsync(test);

        _app.FailOpen = null;
        _reconnect.TryNow(test);
        await SettleAsync();

        Assert.True(test.Active.IsConnected);
        Assert.Null(_reconnect.RunOf(test));
    }

    [Fact]
    public async Task A_connection_closed_while_waiting_is_not_tried_again()
    {
        await SwitchOnAsync();
        var test = await _app.OpenAsync(TestApp.Profile("Test"));
        _app.FailOpen = NoListener;
        await LoseAsync(test);
        var attempts = _app.OpenAttempts;

        await _app.Lifecycle.DisconnectAsync(test);
        await AdvanceAsync(ReconnectPolicy.Pauses[0]);

        Assert.Equal(attempts, _app.OpenAttempts);
    }

    /// <summary>ADR 0008: unlocking is never kept – connected again, the workspace is locked, and the notice says so.</summary>
    [Fact]
    public async Task An_unlocked_workspace_is_read_only_again_and_the_notice_says_so()
    {
        await SwitchOnAsync();
        var prod = await _app.OpenAsync(TestApp.Profile("Prod", ConnectionKind.Prod));
        var workspace = prod.Workspaces.Active!;
        await _app.Lifecycle.UnlockAsync(workspace.Id);
        Assert.True(prod.Workspaces.IsUnlocked(workspace.Id));

        await LoseAsync(prod);

        Assert.True(prod.Active.IsConnected);
        Assert.False(prod.Workspaces.IsUnlocked(workspace.Id));
        Assert.Equal([TextFormat.Format(ShellText.AutoReconnect_LockedAgain, workspace.Name)], _app.Shell.Notice!.Warnings);
    }

    public async ValueTask DisposeAsync()
    {
        _reconnect.Dispose();
        await _app.DisposeAsync();
    }
}
