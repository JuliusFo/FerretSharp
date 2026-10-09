using System.Globalization;

namespace FerretSharp.Core.Resources;

/// <summary>
/// Fills the placeholders of a localized text (WP-29, ADR 0017). The UI language only picks the text; numbers and dates
/// keep the culture they were formatted with before (the current culture, or the German one where the code names it).
/// </summary>
public static class TextFormat
{
    /// <summary><see cref="string.Format(IFormatProvider, string, object[])"/> with the current culture.</summary>
    public static string Format(string format, params object?[] args) => string.Format(CultureInfo.CurrentCulture, format, args);

    /// <summary><see cref="string.Format(IFormatProvider, string, object[])"/> with the given culture.</summary>
    public static string Format(IFormatProvider culture, string format, params object?[] args) => string.Format(culture, format, args);

    /// <summary>
    /// <paramref name="one"/> for exactly one, <paramref name="other"/> otherwise, with the count as <c>{0}</c> and
    /// <paramref name="args"/> from <c>{1}</c> on (English and German only know these two forms).
    /// </summary>
    public static string Plural(long count, string one, string other, params object?[] args) =>
        Plural(CultureInfo.CurrentCulture, count, one, other, args);

    /// <inheritdoc cref="Plural(long, string, string, object[])"/>
    public static string Plural(IFormatProvider culture, long count, string one, string other, params object?[] args) =>
        string.Format(culture, count == 1 ? one : other, [count, .. args]);
}
