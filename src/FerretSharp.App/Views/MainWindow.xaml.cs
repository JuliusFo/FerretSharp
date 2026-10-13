using System.Windows;
using FerretSharp.App.Services;
using FerretSharp.UI.State;

namespace FerretSharp.App.Views;

public partial class MainWindow : Window
{
    private bool _exitApproved;

    public MainWindow(IServiceProvider services, WindowTheme theme, ExitGuard exitGuard)
    {
        theme.PrepareWebViewBackground();
        InitializeComponent();

        WebView.Services = services;
        theme.Attach(this);
        WebView.BlazorWebViewInitialized += (_, e) => theme.AttachWebView(e.WebView.CoreWebView2);

        // Uncommitted changes: the shell asks first (commit / roll back / cancel), then approves the exit.
        Closing += (_, e) =>
        {
            if (!_closeFromApp && !_exitApproved)
            {
                exitGuard.UserClosing(); // the user's own close never installs an update whose restart they cancelled
            }

            if (_exitApproved || exitGuard.CanExit?.Invoke() != false)
            {
                exitGuard.Exiting();
                return;
            }

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
