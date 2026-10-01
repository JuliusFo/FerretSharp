using System.Windows;
using System.Windows.Media;
using AvalonDock;
using AvalonDock.Themes;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace FerretSharp.App.Services;

/// <summary>
/// Follows the Windows light/dark setting and keeps the AvalonDock theme in sync with WPF-UI.
/// </summary>
public sealed class ThemeService
{
    private DockingManager? _dockingManager;

    public void Attach(Window window, DockingManager dockingManager)
    {
        _dockingManager = dockingManager;

        ApplicationThemeManager.Changed += OnThemeChanged;
        ApplicationThemeManager.Apply(GetSystemApplicationTheme(), WindowBackdropType.Mica, updateAccent: true);
        ApplyDockTheme(ApplicationThemeManager.GetAppTheme());

        SystemThemeWatcher.Watch(window);
    }

    private static ApplicationTheme GetSystemApplicationTheme() =>
        ApplicationThemeManager.GetSystemTheme() switch
        {
            SystemTheme.Dark or SystemTheme.HCBlack or SystemTheme.Glow or SystemTheme.CapturedMotion => ApplicationTheme.Dark,
            _ => ApplicationTheme.Light,
        };

    private void OnThemeChanged(ApplicationTheme theme, Color accent) => ApplyDockTheme(theme);

    private void ApplyDockTheme(ApplicationTheme theme)
    {
        if (_dockingManager is null)
        {
            return;
        }

        _dockingManager.Theme = theme == ApplicationTheme.Dark
            ? new Vs2013DarkTheme()
            : new Vs2013LightTheme();
    }
}
