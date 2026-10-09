using System.Globalization;

namespace FerretSharp.Core.Settings;

/// <summary>Language of the user interface (WP-29). The names are the values in <c>settings.json</c> – do not rename.</summary>
public enum UiLanguage
{
    /// <summary>The default, also for settings saved before 3.19 without a language.</summary>
    English,
    German,
}

public static class UiLanguages
{
    private const string Argument = "--lang=";

    /// <summary>The UI culture of a language: the neutral culture, so formats stay with the current culture.</summary>
    public static CultureInfo Culture(UiLanguage language) => CultureInfo.GetCultureInfo(language == UiLanguage.German ? "de" : "en");

    /// <summary>The language the given UI culture shows: German for any German culture, English otherwise.</summary>
    public static UiLanguage Of(CultureInfo uiCulture) =>
        uiCulture.TwoLetterISOLanguageName == "de" ? UiLanguage.German : UiLanguage.English;

    /// <summary>
    /// The language from <c>--lang=en|de</c> on the command line (screenshots, tests): it applies to this session only and
    /// is not saved. Null without the argument or with an unknown value.
    /// </summary>
    public static UiLanguage? FromArguments(IEnumerable<string> args)
    {
        var value = args.LastOrDefault(a => a.StartsWith(Argument, StringComparison.OrdinalIgnoreCase))?[Argument.Length..];
        return value?.ToLowerInvariant() switch
        {
            "en" => UiLanguage.English,
            "de" => UiLanguage.German,
            _ => null,
        };
    }
}
