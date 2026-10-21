using System.Windows;
using FerretSharp.App.Services;
using FerretSharp.UI.State;
using Microsoft.Extensions.Logging;

namespace FerretSharp.App.Views;

public partial class MainWindow : Window
{
    private bool _exitApproved;

    public MainWindow(IServiceProvider services, WindowTheme theme, ExitGuard exitGuard, ILogger<MainWindow> logger)
    {
        theme.PrepareWebViewBackground();
        InitializeComponent();

        WebView.Services = services;
        theme.Attach(this);
        WebView.BlazorWebViewInitialized += (_, e) => theme.AttachWebView(e.WebView.CoreWebView2);

        // Only web links leave the app (release notes, GitHub): BlazorWebView opens every other URL with the system's default
        // handler – a file dropped on the window would be opened or run (review 3.31.0, A39).
        WebView.UrlLoading += (_, e) =>
        {
            if (e.UrlLoadingStrategy == Microsoft.AspNetCore.Components.WebView.UrlLoadingStrategy.OpenExternally
                && e.Url.Scheme != Uri.UriSchemeHttps)
            {
                e.UrlLoadingStrategy = Microsoft.AspNetCore.Components.WebView.UrlLoadingStrategy.CancelLoad;
            }
        };

        // Uncommitted changes: the shell asks first (commit / roll back / cancel), then approves the exit.
        // Who closes is logged: after a render thread failure (2026-10-21) the log could not tell the user's close from the app's.
        Closing += (_, e) =>
        {
            var by = _exitApproved ? "after the question about uncommitted changes" : _closeFromApp ? "by the app" : "by the user";
            if (!_closeFromApp && !_exitApproved)
            {
                exitGuard.UserClosing(); // the user's own close never installs an update whose restart they cancelled
            }

            if (_exitApproved || exitGuard.CanExit?.Invoke() != false)
            {
                logger.LogInformation("Main window closing {By}", by);
                exitGuard.Exiting();
                return;
            }

            logger.LogInformation("Main window closing {By}: asking about uncommitted changes first", by);
            e.Cancel = true;
            exitGuard.RequestExit?.Invoke();
        };
        exitGuard.ExitApproved += () => Dispatcher.BeginInvoke(() =>
        {
            _exitApproved = true;
            Close();
        });
        // The app closes itself (the restart into an update, WP-32) – with the same question as the close button.
        exitGuard.CloseRequested += () => Dispatcher.BeginInvoke(() =>
        {
            _closeFromApp = true;
            try
            {
                Close();
            }
            finally
            {
                _closeFromApp = false;
            }
        });
    }

    /// <summary>Set while <see cref="ExitGuard.RequestClose"/> closes the window, to tell it from the user's close.</summary>
    private bool _closeFromApp;
}
