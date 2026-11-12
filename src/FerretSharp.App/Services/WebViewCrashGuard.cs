using System.Windows.Threading;
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
        if (!browserGone && e.ProcessFailedKind is not (CoreWebView2ProcessFailedKind.RenderProcessExited or CoreWebView2ProcessFailedKind.RenderProcessUnresponsive))
        {
            return; // the GPU and helper processes come back by themselves
        }

        PageDead = true;
        var now = _time.GetUtcNow();
        if (!browserGone && (_reloadedAt is not { } at || now - at > AgainWithin))
        {
            Reload(core, now, "once after a crash");
            return;
        }

        // After the event: a dialog inside it would run while WebView2 is still reporting.
        Dispatcher.CurrentDispatcher.BeginInvoke(() =>
        {
            if (dialogs.Ask(CommonText.WebViewCrash_Title, browserGone ? CommonText.WebViewCrash_BrowserGone : CommonText.WebViewCrash_Again))
            {
                logger.LogInformation("Restarting after the WebView crashed");
                exitGuard.RequestClose(StartAgain ?? (() => { }));
            }
            else if (!browserGone)
            {
                Reload(core, _time.GetUtcNow(), "again, as the user chose");
            }
        });
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
