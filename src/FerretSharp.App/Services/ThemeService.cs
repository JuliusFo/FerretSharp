using System.Windows;
using FerretSharp.Core.Settings;
using FerretSharp.UI.State;
using ThemeMode = FerretSharp.Core.Settings.ThemeMode;

namespace FerretSharp.App.Services;

public sealed class ThemeService(WindowTheme theme, AppSettingsService settings) : IThemeService
{
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
        await settings.UpdateAsync(s => s with { Theme = mode }, CancellationToken.None);
    }
}
