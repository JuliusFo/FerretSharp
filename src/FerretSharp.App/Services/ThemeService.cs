using System.Windows;
using FerretSharp.Core.Settings;
using FerretSharp.UI.State;
using ThemeMode = FerretSharp.Core.Settings.ThemeMode;

namespace FerretSharp.App.Services;

public sealed class ThemeService(WindowTheme theme, SettingsStore store, AppSettings initial) : IThemeService
{
    private AppSettings _settings = initial;

    public ThemeMode Mode => theme.Mode;

    public string? SessionOverride => theme.SessionOverride switch
    {
        true => "dark",
        false => "light",
        null => null,
    };

    public async Task SetModeAsync(ThemeMode mode)
    {
        await Application.Current.Dispatcher.InvokeAsync(() => theme.SetMode(mode));
        _settings = _settings with { Theme = mode };
        await store.SaveAsync(_settings, CancellationToken.None);
    }
}
