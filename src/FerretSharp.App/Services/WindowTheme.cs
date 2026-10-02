using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using FerretSharp.Core.Settings;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;
using ThemeMode = FerretSharp.Core.Settings.ThemeMode;

namespace FerretSharp.App.Services;

/// <summary>
/// Light/dark for the whole window: native chrome (title bar, background behind the WebView) and the WebView's
/// <c>prefers-color-scheme</c>, which the CSS and the grid follow. The mode comes from the settings and can change at
/// runtime; <c>--theme=dark|light</c> overrides it for the session (tests, screenshots).
/// </summary>
public sealed class WindowTheme
{
    private const int DwmUseImmersiveDarkMode = 20;
    private static readonly Color DarkBackground = Color.FromRgb(0x14, 0x15, 0x18);
    private static readonly Color LightBackground = Color.FromRgb(0xf4, 0xf4, 0xf6);

    private Window? _window;
    private CoreWebView2? _webView;

    private WindowTheme(bool? sessionOverride, ThemeMode mode)
    {
        SessionOverride = sessionOverride;
        Mode = mode;
    }

    /// <summary>true/false when forced via <c>--theme=dark|light</c>; null otherwise.</summary>
    public bool? SessionOverride { get; }

    /// <summary>The saved choice (System follows Windows).</summary>
    public ThemeMode Mode { get; private set; }

    public bool IsDark => SessionOverride ?? Mode switch
    {
        ThemeMode.Dark => true,
        ThemeMode.Light => false,
        _ => IsSystemDark(),
    };

    public static WindowTheme Create(IEnumerable<string> args, ThemeMode mode)
    {
        var value = args.FirstOrDefault(a => a.StartsWith("--theme=", StringComparison.OrdinalIgnoreCase))?["--theme=".Length..];
        return new WindowTheme(value?.ToLowerInvariant() switch
        {
            "dark" => true,
            "light" => false,
            _ => null,
        }, mode);
    }

    /// <summary>Must run before the WebView2 environment is created to avoid a white flash on startup.</summary>
    public void PrepareWebViewBackground()
    {
        var c = IsDark ? DarkBackground : LightBackground;
        Environment.SetEnvironmentVariable("WEBVIEW2_DEFAULT_BACKGROUND_COLOR", $"FF{c.R:X2}{c.G:X2}{c.B:X2}");
    }

    public void Attach(Window window)
    {
        _window = window;
        ApplyWindow();
        window.SourceInitialized += (_, _) => ApplyWindow();

        // In System mode the title bar follows Windows; the WebView does so by itself (PreferredColorScheme.Auto).
        UserPreferenceChangedEventHandler handler = (_, e) =>
        {
            if (e.Category == UserPreferenceCategory.General)
            {
                window.Dispatcher.Invoke(ApplyWindow);
            }
        };
        SystemEvents.UserPreferenceChanged += handler;
        window.Closed += (_, _) => SystemEvents.UserPreferenceChanged -= handler;
    }

    public void AttachWebView(CoreWebView2 webView)
    {
        _webView = webView;
        ApplyWebView();
    }

    /// <summary>Switches immediately (UI thread). With a session override the window keeps the forced theme.</summary>
    public void SetMode(ThemeMode mode)
    {
        Mode = mode;
        ApplyWindow();
        ApplyWebView();
    }

    private void ApplyWindow()
    {
        if (_window is not { } window)
        {
            return;
        }

        var dark = IsDark;
        window.Background = new SolidColorBrush(dark ? DarkBackground : LightBackground);

        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd != IntPtr.Zero)
        {
            var value = dark ? 1 : 0;
            _ = DwmSetWindowAttribute(hwnd, DwmUseImmersiveDarkMode, ref value, sizeof(int));
        }
    }

    private void ApplyWebView()
    {
        if (_webView is null)
        {
            return;
        }

        _webView.Profile.PreferredColorScheme = SessionOverride switch
        {
            true => CoreWebView2PreferredColorScheme.Dark,
            false => CoreWebView2PreferredColorScheme.Light,
            null => Mode switch
            {
                ThemeMode.Dark => CoreWebView2PreferredColorScheme.Dark,
                ThemeMode.Light => CoreWebView2PreferredColorScheme.Light,
                _ => CoreWebView2PreferredColorScheme.Auto,
            },
        };
    }

    private static bool IsSystemDark()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is 0;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
