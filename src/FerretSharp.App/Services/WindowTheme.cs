using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace FerretSharp.App.Services;

/// <summary>
/// Keeps the native window chrome (title bar, background behind the WebView) in line with the light/dark theme.
/// The web content itself follows <c>prefers-color-scheme</c>; an explicit override is passed to WebView2.
/// </summary>
public sealed class WindowTheme
{
    private const int DwmUseImmersiveDarkMode = 20;
    private static readonly Color DarkBackground = Color.FromRgb(0x14, 0x15, 0x18);
    private static readonly Color LightBackground = Color.FromRgb(0xf4, 0xf4, 0xf6);

    private WindowTheme(bool? forceDark) => ForceDark = forceDark;

    /// <summary>null = follow Windows; true/false = forced via <c>--theme=dark|light</c>.</summary>
    public bool? ForceDark { get; }

    public bool IsDark => ForceDark ?? IsSystemDark();

    public static WindowTheme FromArgs(IEnumerable<string> args)
    {
        var value = args.FirstOrDefault(a => a.StartsWith("--theme=", StringComparison.OrdinalIgnoreCase))?["--theme=".Length..];
        return new WindowTheme(value?.ToLowerInvariant() switch
        {
            "dark" => true,
            "light" => false,
            _ => null,
        });
    }

    /// <summary>Must run before the WebView2 environment is created to avoid a white flash on startup.</summary>
    public void PrepareWebViewBackground()
    {
        var c = IsDark ? DarkBackground : LightBackground;
        Environment.SetEnvironmentVariable("WEBVIEW2_DEFAULT_BACKGROUND_COLOR", $"FF{c.R:X2}{c.G:X2}{c.B:X2}");
    }

    public void Attach(Window window)
    {
        Apply(window);
        window.SourceInitialized += (_, _) => Apply(window);

        if (ForceDark is null)
        {
            UserPreferenceChangedEventHandler handler = (_, e) =>
            {
                if (e.Category == UserPreferenceCategory.General)
                {
                    window.Dispatcher.Invoke(() => Apply(window));
                }
            };
            SystemEvents.UserPreferenceChanged += handler;
            window.Closed += (_, _) => SystemEvents.UserPreferenceChanged -= handler;
        }
    }

    private void Apply(Window window)
    {
        var dark = IsDark;
        window.Background = new SolidColorBrush(dark ? DarkBackground : LightBackground);

        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd != IntPtr.Zero)
        {
            var value = dark ? 1 : 0;
            _ = DwmSetWindowAttribute(hwnd, DwmUseImmersiveDarkMode, ref value, sizeof(int));
        }
    }

    private static bool IsSystemDark()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is 0;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
