using FerretSharp.Core.Settings;
using FerretSharp.UI.Resources;
using FerretSharp.UI.State;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace FerretSharp.UI.Tests.State;

/// <summary>WP-32: the app only reports updates; downloading and installing wait for the click, the restart goes through the ExitGuard.</summary>
public sealed class UpdateStateTests : IDisposable
{
    private static readonly AppUpdate Next = new("3.29.0", "### Added\n- Updates from the app");

    private readonly TestFolder _folder = new();
    private readonly FakeUpdater _updater = new();
    private readonly ExitGuard _exitGuard = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 13, 9, 30, 0, TimeSpan.Zero));
    private readonly AppSettingsService _settings;

    public UpdateStateTests() => _settings = new AppSettingsService(new SettingsStore(_folder.Combine("settings.json")), new AppSettings());

    public void Dispose() => _folder.Dispose();

    private UpdateState Create() => new(_updater, _settings, _exitGuard, NullLogger<UpdateState>.Instance, _time);

    [Fact]
    public async Task A_copy_that_was_not_installed_never_checks()
    {
        _updater.IsInstalled = false;
        var updates = Create();

        await updates.StartAsync();
        await updates.CheckAsync();

        Assert.Equal(UpdatePhase.NotInstalled, updates.Phase);
        Assert.False(updates.CanCheck);
        Assert.Equal(0, _updater.Checks);
    }

    [Fact]
    public async Task The_check_at_start_reports_a_newer_version_with_its_notes_and_downloads_nothing()
    {
        _updater.Found = Next;
        var updates = Create();

        await updates.StartAsync();

        Assert.Equal(UpdatePhase.Available, updates.Phase);
        Assert.Equal(Next, updates.Update);
        Assert.Equal(_time.GetLocalNow(), updates.LastChecked);
        Assert.Empty(_updater.Downloads);
    }

    [Fact]
    public async Task With_the_check_at_start_off_only_check_now_asks()
    {
        await _settings.UpdateAsync(s => s with { CheckForUpdates = false }, TestContext.Current.CancellationToken);
        var updates = Create();

        await updates.StartAsync();
        Assert.Equal(UpdatePhase.Idle, updates.Phase);
        Assert.Equal(0, _updater.Checks);

        await updates.CheckAsync();

        Assert.Equal(UpdatePhase.UpToDate, updates.Phase);
        Assert.Null(updates.Update);
        Assert.Equal(1, _updater.Checks);
    }

    [Fact]
    public async Task A_failed_check_is_a_note_and_can_be_tried_again()
    {
        _updater.CheckFails = new HttpRequestException("No such host is known.");
        var updates = Create();

        await updates.StartAsync();

        Assert.Equal(UpdatePhase.CheckFailed, updates.Phase);
        Assert.Equal("No such host is known.", updates.Error);
        Assert.True(updates.CanCheck);

        _updater.CheckFails = null;
        _updater.Found = Next;
        await updates.CheckAsync();

        Assert.Equal(UpdatePhase.Available, updates.Phase);
        Assert.Null(updates.Error);
    }

    [Fact]
    public async Task A_check_without_answer_gives_up()
    {
        _updater.CheckHangs = true;
        var updates = Create();

        var check = updates.CheckAsync();
        Assert.Equal(UpdatePhase.Checking, updates.Phase);
        Assert.False(updates.CanCheck);
        _time.Advance(UpdateState.CheckTimeout);
        await check;

        Assert.Equal(UpdatePhase.CheckFailed, updates.Phase);
        Assert.Equal(ShellText.Update_CheckTimedOut, updates.Error);
    }

    [Fact]
    public async Task A_download_left_from_the_last_session_waits_for_the_restart_without_checking()
    {
        _updater.Downloaded = Next;
        var updates = Create();

        await updates.StartAsync();

        Assert.Equal(UpdatePhase.Ready, updates.Phase);
        Assert.Equal(Next, updates.Update);
        Assert.False(updates.CanCheck);
        Assert.Equal(0, _updater.Checks);
    }

    [Fact]
    public async Task Update_downloads_with_progress_then_closes_the_app_and_installs_only_once_it_ends()
    {
        _updater.Found = Next;
        _updater.Progress = [10, 55, 100];
        var updates = Create();
        var progress = new List<int>();
        updates.Changed += () => progress.Add(updates.Progress);
        var closeRequests = 0;
        _exitGuard.CloseRequested += () => closeRequests++;
        await updates.CheckAsync();
        updates.ShowDetails();

        await updates.UpdateAsync();

        Assert.Equal([Next], _updater.Downloads);
        Assert.Contains(55, progress);
        Assert.Equal(UpdatePhase.Ready, updates.Phase);
        Assert.False(updates.DetailsOpen); // the question about uncommitted changes must not open behind it
        Assert.Equal(1, closeRequests);
        Assert.Empty(_updater.Applied);

        _exitGuard.Exiting();

        Assert.Equal([Next], _updater.Applied);
    }

    [Fact]
    public async Task A_restart_the_user_cancels_does_not_install_when_they_close_later_themselves()
    {
        _updater.Downloaded = Next;
        var updates = Create();
        await updates.StartAsync();

        updates.Restart();   // the question about uncommitted changes comes …
        _exitGuard.UserClosing(); // … is cancelled, and later the user closes the window
        _exitGuard.Exiting();

        Assert.Empty(_updater.Applied);
        Assert.Equal(UpdatePhase.Ready, updates.Phase);
    }

    [Fact]
    public async Task A_canceled_download_leaves_the_update_available()
    {
        _updater.Found = Next;
        _updater.DownloadHangs = true;
        var updates = Create();
        var closeRequests = 0;
        _exitGuard.CloseRequested += () => closeRequests++;
        await updates.CheckAsync();

        var download = updates.UpdateAsync();
        Assert.Equal(UpdatePhase.Downloading, updates.Phase);
        updates.CancelDownload();
        await download;

        Assert.Equal(UpdatePhase.Available, updates.Phase);
        Assert.Equal(0, closeRequests);
    }

    [Fact]
    public async Task A_failed_download_says_why_and_can_be_tried_again()
    {
        _updater.Found = Next;
        _updater.DownloadFails = new IOException("Disk full");
        var updates = Create();
        await updates.CheckAsync();

        await updates.UpdateAsync();

        Assert.Equal(UpdatePhase.DownloadFailed, updates.Phase);
        Assert.Equal("Disk full", updates.Error);

        _updater.DownloadFails = null;
        await updates.UpdateAsync();

        Assert.Equal(UpdatePhase.Ready, updates.Phase);
        Assert.Equal(2, _updater.Downloads.Count);
    }

    [Fact]
    public async Task An_updater_that_cannot_start_does_not_stop_the_app_from_ending()
    {
        _updater.Downloaded = Next;
        _updater.ApplyFails = true;
        var updates = Create();
        await updates.StartAsync();

        updates.Restart();
        _exitGuard.Exiting();

        Assert.Equal([Next], _updater.Applied);
    }

    [Fact]
    public void The_dialog_opens_only_with_an_update()
    {
        var updates = Create();

        updates.ShowDetails();

        Assert.False(updates.DetailsOpen);
    }

    private sealed class FakeUpdater : IAppUpdater
    {
        public bool IsInstalled { get; set; } = true;

        public AppUpdate? Downloaded { get; set; }

        public AppUpdate? Found { get; set; }

        public Exception? CheckFails { get; set; }

        public bool CheckHangs { get; set; }

        public int Checks { get; private set; }

        public int[] Progress { get; set; } = [100];

        public Exception? DownloadFails { get; set; }

        public bool DownloadHangs { get; set; }

        public List<AppUpdate> Downloads { get; } = [];

        public bool ApplyFails { get; set; }

        public List<AppUpdate> Applied { get; } = [];

        public async Task<AppUpdate?> CheckAsync(CancellationToken cancellationToken)
        {
            Checks++;
            if (CheckHangs)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            return CheckFails is { } fails ? throw fails : Found;
        }

        public async Task DownloadAsync(AppUpdate update, Action<int> progress, CancellationToken cancellationToken)
        {
            Downloads.Add(update);
            if (DownloadHangs)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            if (DownloadFails is { } fails)
            {
                throw fails;
            }

            foreach (var percent in Progress)
            {
                progress(percent);
            }
        }

        public void ApplyAfterExit(AppUpdate update)
        {
            Applied.Add(update);
            if (ApplyFails)
            {
                throw new InvalidOperationException("Update.exe not found");
            }
        }
    }
}
