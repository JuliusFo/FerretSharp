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
            if (_exitApproved || exitGuard.CanExit?.Invoke() != false)
            {
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
    }
}
