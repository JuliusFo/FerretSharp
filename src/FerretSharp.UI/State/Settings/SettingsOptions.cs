using FerretSharp.Core.Resources;
using FerretSharp.Core.Settings;
using FerretSharp.UI.Resources;

namespace FerretSharp.UI.State;

/// <summary>The values to choose from on the settings page, with their labels (R6b, B9: out of the page's code).</summary>
public static class SettingsOptions
{
    public static IReadOnlyList<(bool On, string Label)> OnOff => [(true, CommonText.On), (false, CommonText.Off)];

    /// <summary>Each language under its own name, in every UI language.</summary>
    public static readonly IReadOnlyList<(UiLanguage Language, string Label)> Languages = [(UiLanguage.English, "English"), (UiLanguage.German, "Deutsch")];

    public static IReadOnlyList<(ThemeMode Mode, string Label)> Themes =>
        [(ThemeMode.System, ShellText.Settings_ThemeSystem), (ThemeMode.Light, ShellText.Settings_ThemeLight), (ThemeMode.Dark, ShellText.Settings_ThemeDark)];

    public static IReadOnlyList<(ClrNameDisplay Mode, string Label)> ClrNames =>
        [(ClrNameDisplay.Off, CommonText.Off), (ClrNameDisplay.Beside, ShellText.Settings_ClrNamesBeside), (ClrNameDisplay.Front, ShellText.Settings_ClrNamesFront)];

    public static IReadOnlyList<(int Seconds, string Label)> LockWaits => [.. new[] { 1, 3, 5, 10, 30 }.Select(s => (s, $"{s} s"))];

    public static IReadOnlyList<(FkCountMode Mode, string Label)> FkCounts =>
        [(FkCountMode.Immediately, ShellText.Settings_FkCountsImmediately), (FkCountMode.OnHover, ShellText.Settings_FkCountsOnHover)];

    public static IReadOnlyList<(int Hours, string Label)> UpdateIntervals =>
    [
        (0, ShellText.Settings_UpdateCheckNever), (1, ShellText.Settings_UpdateCheckHourly),
        (4, ShellText.Settings_UpdateCheckEvery4Hours), (24, ShellText.Settings_UpdateCheckDaily),
    ];
}
