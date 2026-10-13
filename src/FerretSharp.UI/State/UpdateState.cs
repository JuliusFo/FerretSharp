using FerretSharp.Core.Settings;
using FerretSharp.UI.Resources;
using Microsoft.Extensions.Logging;

namespace FerretSharp.UI.State;

public enum UpdatePhase
{
    /// <summary>Not an installed FerretSharp (a build, the plain zip): no checks, Settings › Updates says why.</summary>
    NotInstalled,

    /// <summary>Not checked in this session (the check at start is off).</summary>
    Idle,

    Checking,

    UpToDate,

    /// <summary>A newer version is out; the status bar says so.</summary>
    Available,

    Downloading,

    /// <summary>Downloaded; a restart installs it.</summary>
    Ready,

    /// <summary>The check failed (offline, VPN, rate limit): a harmless note in the settings, nothing in the status bar.</summary>
    CheckFailed,

    /// <summary>The download failed; the status bar offers to try again.</summary>
    DownloadFailed,
}

/// <summary>
/// Update of the app (WP-32, ADR 0024): only reports. The check at start and while the app runs (if on, every
/// <see cref="AppSettings.UpdateCheckHours"/>) and "Check now" look for a newer version; downloading waits for the click on
/// "Download", installing for a second click on "Restart now", which ends the app through the <see cref="ExitGuard"/> –
/// uncommitted changes are asked about first, as when closing the window.
/// </summary>
public sealed class UpdateState(IAppUpdater updater, AppSettingsService settings, ExitGuard exitGuard, ILogger<UpdateState> logger, TimeProvider? time = null) : IDisposable
{
    /// <summary>A check that gets no answer (a stalled VPN) ends after this; the next start or "Check now" tries again.</summary>
    public static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How often the app looks whether the next check while it runs is due. The interval counts from the last check on the
    /// clock, not in timer ticks: a check that is due after a sleep of the PC comes within this time after waking up, and a
    /// changed setting needs no new timer.
    /// </summary>
    public static readonly TimeSpan BackgroundTick = TimeSpan.FromMinutes(5);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private CancellationTokenSource? _download;
    private ITimer? _background;
    private DateTimeOffset _started;
    private int _checking;

    public UpdatePhase Phase { get; private set; } = updater.IsInstalled ? UpdatePhase.Idle : UpdatePhase.NotInstalled;

    /// <summary>The newer version (available, downloading, ready or failed to download); null otherwise.</summary>
    public AppUpdate? Update { get; private set; }

    /// <summary>Download progress, 0–100.</summary>
    public int Progress { get; private set; }

    /// <summary>Why the last check or download failed.</summary>
    public string? Error { get; private set; }

    /// <summary>When the last check ended (also a failed one); null if none in this session.</summary>
    public DateTimeOffset? LastChecked { get; private set; }

    /// <summary>"Check now" is possible: installed, nothing running, nothing downloaded waiting for the restart.</summary>
    public bool CanCheck => Phase is UpdatePhase.Idle or UpdatePhase.UpToDate or UpdatePhase.Available or UpdatePhase.CheckFailed or UpdatePhase.DownloadFailed;

    /// <summary>The update dialog (release notes, "Download", "Restart now") is open – from the status bar or Settings › Updates.</summary>
    public bool DetailsOpen { get; private set; }

    /// <summary>Raised on any change, on the thread that made it (the download's progress comes from the thread pool).</summary>
    public event Action? Changed;

    public void ShowDetails()
    {
        if (Update is not null)
        {
            DetailsOpen = true;
            Changed?.Invoke();
        }
    }

    /// <summary>Closes the dialog; a running download goes on (the status bar shows it).</summary>
    public void HideDetails()
    {
        DetailsOpen = false;
        Changed?.Invoke();
    }

    /// <summary>
    /// At start: a download left from the last session waits for the restart; otherwise check if the setting says so. Then
    /// the checks while the app runs begin (<see cref="BackgroundTick"/>).
    /// </summary>
    public async Task StartAsync()
    {
        if (Phase != UpdatePhase.Idle || _background is not null)
        {
            return;
        }

        _started = _time.GetLocalNow();
        _background = _time.CreateTimer(_ => CheckIfDue(), null, BackgroundTick, BackgroundTick);
        if (updater.Downloaded is { } downloaded)
        {
            Update = downloaded;
            Set(UpdatePhase.Ready);
            return;
        }

        if (settings.Current.CheckForUpdates)
        {
            await CheckAsync();
        }
    }

    /// <summary>Looks for a newer version. Never throws: a failure is <see cref="UpdatePhase.CheckFailed"/> with <see cref="Error"/>.</summary>
    public async Task CheckAsync()
    {
        // "Check now" on the UI thread and a check while running on the timer's thread: one at a time.
        if (!CanCheck || Interlocked.Exchange(ref _checking, 1) == 1)
        {
            return;
        }

        Error = null;
        Set(UpdatePhase.Checking);
        try
        {
            var found = await updater.CheckAsync(CancellationToken.None).WaitAsync(CheckTimeout, _time);
            LastChecked = _time.GetLocalNow();
            Update = found;
            if (found is not null)
            {
                logger.LogInformation("Update {Version} is available", found.Version);
            }

            Set(found is null ? UpdatePhase.UpToDate : UpdatePhase.Available);
        }
        catch (Exception ex)
        {
            logger.LogInformation(ex, "Update check failed");
            LastChecked = _time.GetLocalNow();
            Update = null;
            Error = ex is TimeoutException ? ShellText.Update_CheckTimedOut : ex.Message;
            Set(UpdatePhase.CheckFailed);
        }
        finally
        {
            Volatile.Write(ref _checking, 0);
        }
    }

    /// <summary>
    /// The check while the app runs, if it is on and due: the interval has passed since the last check (or the start). Only
    /// while no update is known – one that is available, downloading or waiting for the restart has been reported already –
    /// and not while the dialog is open.
    /// </summary>
    private void CheckIfDue()
    {
        var current = settings.Current;
        if (!current.CheckForUpdates || current.UpdateCheckHours <= 0 || DetailsOpen
            || Phase is not (UpdatePhase.Idle or UpdatePhase.UpToDate or UpdatePhase.CheckFailed))
        {
            return;
        }

        if (_time.GetLocalNow() - (LastChecked ?? _started) >= TimeSpan.FromHours(current.UpdateCheckHours))
        {
            _ = CheckAsync(); // never throws: a failed check is a note in the settings
        }
    }

    public void Dispose() => _background?.Dispose();

    /// <summary>
    /// The click on "Download": downloads (cancellable, <see cref="CancelDownload"/>); then the update waits for "Restart now"
    /// (<see cref="Restart"/>, the user's decision, WP-32). Never throws: a failed download is <see cref="UpdatePhase.DownloadFailed"/>.
    /// </summary>
    public async Task DownloadAsync()
    {
        if (Update is not { } update || Phase is not (UpdatePhase.Available or UpdatePhase.DownloadFailed))
        {
            return;
        }

        var download = _download = new CancellationTokenSource();
        Progress = 0;
        Error = null;
        Set(UpdatePhase.Downloading);
        try
        {
            await updater.DownloadAsync(update, ReportProgress, download.Token);
        }
        catch (OperationCanceledException) when (download.IsCancellationRequested)
        {
            logger.LogInformation("Download of update {Version} canceled", update.Version);
            Set(UpdatePhase.Available);
            return;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Download of update {Version} failed", update.Version);
            Error = ex.Message;
            Set(UpdatePhase.DownloadFailed);
            return;
        }
        finally
        {
            _download = null;
            download.Dispose();
        }

        logger.LogInformation("Update {Version} downloaded", update.Version);
        Progress = 100;
        Set(UpdatePhase.Ready);
    }

    public void CancelDownload() => _download?.Cancel();

    /// <summary>
    /// Ends the app as the window's close button does (asking about uncommitted changes first); once it really ends, the
    /// updater installs the downloaded version and starts it. Cancelling the question keeps the download for later.
    /// </summary>
    public void Restart()
    {
        if (Phase != UpdatePhase.Ready || Update is not { } update)
        {
            return;
        }

        // The question about uncommitted changes comes next and must not open behind the update dialog.
        DetailsOpen = false;
        Changed?.Invoke();
        exitGuard.RequestClose(() =>
        {
            try
            {
                logger.LogInformation("Restarting into update {Version}", update.Version);
                updater.ApplyAfterExit(update);
            }
            catch (Exception ex)
            {
                // The app ends anyway; the download stays and is offered again at the next start.
                logger.LogError(ex, "Starting the update to {Version} failed", update.Version);
            }
        });
    }

    private void ReportProgress(int percent)
    {
        percent = Math.Clamp(percent, 0, 100);
        if (percent != Progress)
        {
            Progress = percent;
            Changed?.Invoke();
        }
    }

    private void Set(UpdatePhase phase)
    {
        Phase = phase;
        Changed?.Invoke();
    }
}
