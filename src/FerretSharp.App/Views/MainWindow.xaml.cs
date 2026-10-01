using System.Windows;
using FerretSharp.App.Services;
using Microsoft.Web.WebView2.Core;

namespace FerretSharp.App.Views;

public partial class MainWindow : Window
{
    public MainWindow(IServiceProvider services, WindowTheme theme)
    {
        theme.PrepareWebViewBackground();
        InitializeComponent();

        WebView.Services = services;
        theme.Attach(this);

        if (theme.ForceDark is { } dark)
        {
            WebView.BlazorWebViewInitialized += (_, e) =>
                e.WebView.CoreWebView2.Profile.PreferredColorScheme =
                    dark ? CoreWebView2PreferredColorScheme.Dark : CoreWebView2PreferredColorScheme.Light;
        }
    }
}
