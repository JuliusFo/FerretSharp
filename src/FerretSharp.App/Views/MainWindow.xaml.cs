using System.Windows;
using FerretSharp.App.Services;

namespace FerretSharp.App.Views;

public partial class MainWindow : Window
{
    public MainWindow(IServiceProvider services, WindowTheme theme)
    {
        theme.PrepareWebViewBackground();
        InitializeComponent();

        WebView.Services = services;
        theme.Attach(this);
        WebView.BlazorWebViewInitialized += (_, e) => theme.AttachWebView(e.WebView.CoreWebView2);
    }
}
