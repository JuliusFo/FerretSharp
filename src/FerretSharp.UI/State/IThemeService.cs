using FerretSharp.Core.Settings;

namespace FerretSharp.UI.State;

/// <summary>Light/dark choice of the app; implemented by the host, which owns the window and the WebView.</summary>
public interface IThemeService
{
    /// <summary>The saved choice.</summary>
    ThemeMode Mode { get; }

    /// <summary>"dark"/"light" if <c>--theme</c> forces the theme for this session; the saved choice applies from the next start.</summary>
    string? SessionOverride { get; }

    /// <summary>Saves the choice and applies it immediately (unless overridden for the session).</summary>
    /// <exception cref="IOException">The settings file could not be written; the theme is applied anyway.</exception>
    Task SetModeAsync(ThemeMode mode);
}
