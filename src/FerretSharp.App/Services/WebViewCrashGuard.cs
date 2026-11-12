using System.Windows.Threading;
using FerretSharp.Core.Resources;
using FerretSharp.UI.Resources;
using FerretSharp.UI.State;
using Microsoft.Extensions.Logging;
using Microsoft.Web.WebView2.Core;

namespace FerretSharp.App.Services;

/// <summary>
/// What happens when the WebView's renderer crashes (review 3.70.0, A2, decision of the user): the page is reloaded once –
/// workspaces, sessions and transactions live in the connections' scopes of the singleton <c>ConnectionHub</c>, not in the
/// page's – and if it crashes again shortly after, a native question offers to restart FerretSharp. Without this the page
/// stayed empty, and with an open transaction the window could not be closed: the shell asked in the dead page.
/// </summary>
public sealed class WebViewCrashGuard(IDialogService dialogs, ExitGuard exitGuard, ILogger<WebViewCrashGuard> logger, TimeProvider? time = null)
{
    /// <summary>A crash within this time after the automatic reload asks instead of reloading again (no loop).</summary>
    internal static readonly TimeSpan AgainWithin = TimeSpan.FromMinutes(2);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private DateTimeOffset? _reloadedAt;

    /// <summary>The page has crashed and not loaded again: closing asks natively (<see cref="MainWindow"/>).</summary>
    public bool PageDead { get; private set; }

    /// <summary>
    /// Changes are being written before a reload, or committed or rolled back before closing: a close meanwhile waits
    /// instead of asking again and closing in the middle (code review of R9, finding 2 and its second pass).
    /// </summary>
    public bool Busy { get; private set; }

    /// <summary>Set by the app: starts FerretSharp again once this one has ended.</summary>
    public Action? StartAgain { get; set; }

    public void Attach(CoreWebView2 core)
    {
        ArgumentNullException.ThrowIfNull(core);
        core.ProcessFailed += (_, e) => OnProcessFailed(core, e);
        core.NavigationCompleted += (_, e) =>
        {
            if (e.IsSuccess)
            {
                PageDead = false;
            }
        };
    }

    private void OnProcessFailed(CoreWebView2 core, CoreWebView2ProcessFailedEventArgs e)
    {
        logger.LogError("WebView process failed: {Kind}, reason {Reason}, exit code {ExitCode}, {Process}",
            e.ProcessFailedKind, e.Reason, e.ExitCode, e.ProcessDescription);
        var browserGone = e.ProcessFailedKind == CoreWebView2ProcessFailedKind.BrowserProcessExited;
        // A renderer that does not answer may come back (code review of R9, finding 1); the GPU and helper processes come
        // back by themselves.
        if (!browserGone && e.ProcessFailedKind != CoreWebView2ProcessFailedKind.RenderProcessExited)
        {
            return;
        }

        PageDead = true;
        var now = _time.GetUtcNow();
        if (!browserGone && (_reloadedAt is not { } at || now - at > AgainWithin))
        {
            _reloadedAt = now; // a second crash while the question below is open asks about the restart
            Dispatcher.CurrentDispatcher.BeginInvoke(() => Logged(() => ReloadAsync(core, "once after a crash")));
            return;
        }

        // After the event: a dialog inside it would run while WebView2 is still reporting.
        Dispatcher.CurrentDispatcher.BeginInvoke(() => Logged(async () =>
        {
            if (dialogs.Ask(CommonText.WebViewCrash_Title, browserGone ? CommonText.WebViewCrash_BrowserGone : CommonText.WebViewCrash_Again))
            {
                logger.LogInformation("Restarting after the WebView crashed");
                exitGuard.RequestClose(StartAgain ?? (() => { }));
            }
            else if (!browserGone)
            {
                await ReloadAsync(core, "again, as the user chose");
            }
        }));
    }

    /// <summary>
    /// Loads the page anew – after asking natively if grid changes are not written yet: they live in the page and would go
    /// without a word (code review of R9, finding 1). Write them (no commit) and load, discard them and load, or leave the
    /// page dead – closing then asks about the changes.
    /// </summary>
    private async Task ReloadAsync(CoreWebView2 core, string why)
    {
        if (Busy)
        {
            // A native commit or rollback runs: a reload now would also clear its hold on closing (third pass of the code review).
            logger.LogInformation("Not reloading the page: changes are being written, committed or rolled back");
            return;
        }

        Busy = true;
        CrashReloadDecision decision;
        try
        {
            decision = await CrashReload.DecideAsync(
                exitGuard,
                pending => dialogs.AskYesNoCancel(CommonText.WebViewCrash_Title, TextFormat.Plural(pending, CommonText.WebViewCrash_PendingOne, CommonText.WebViewCrash_PendingOther)),
                () => dialogs.Ask(CommonText.WebViewCrash_Title, CommonText.WebViewCrash_FlushFailed));
        }
        finally
        {
            Busy = false;
        }

        if (decision.Pending > 0)
        {
            logger.LogWarning("Pending grid changes before reloading the page: {Pending}, {Choice}", decision.Pending, decision.Choice);
        }

        if (!decision.Reload)
        {
            return;
        }

        Reload(core, _time.GetUtcNow(), why);
    }

    /// <summary>
    /// Runs <paramref name="work"/> after the event, an exception in the log: a task returned to <c>BeginInvoke</c> is never
    /// observed, the page would stay dead without a word (second pass of the code review of R9).
    /// </summary>
    internal async Task Logged(Func<Task> work)
    {
        try
        {
            await work();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Handling the WebView crash failed");
        }
    }

    private void Reload(CoreWebView2 core, DateTimeOffset now, string why)
    {
        _reloadedAt = now;
        logger.LogWarning("Reloading the page {Why}", why);
        try
        {
            core.Reload();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            logger.LogError(ex, "Reloading the page failed");
        }
    }

    /// <summary>
    /// The window closes with uncommitted work while the page is dead: asks natively (commit, roll back, keep open) and
    /// finishes through the shell. True if the window may close now.
    /// </summary>
    public async Task<bool> FinishNativelyAsync()
    {
        if (Busy)
        {
            logger.LogInformation("Closing waits: the changes are being written, committed or rolled back");
            return false;
        }

        Busy = true;
        try
        {
            return await FinishCoreAsync();
        }
        finally
        {
            Busy = false;
        }
    }

    private async Task<bool> FinishCoreAsync()
    {
        if (dialogs.AskYesNoCancel(CommonText.WebViewCrash_Title, CommonText.WebViewCrash_Close) is not { } commit)
        {
            return false;
        }

        if (exitGuard.FinishAll is { } finish && await finish(commit))
        {
            return true;
        }

        logger.LogWarning("Closing after the WebView crashed: {What} did not finish", commit ? "commit" : "rollback");
        return dialogs.Ask(CommonText.WebViewCrash_Title, CommonText.WebViewCrash_FinishFailed);
    }
}
