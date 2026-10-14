using System.Globalization;
using FerretSharp.Core.Data;

namespace FerretSharp.Core.Query;

/// <summary>
/// Values as the user types them – in the filter bar, a grid cell, a SQL variable, the LINQ filter: numbers in German or
/// invariant notation, hex digits, dates in German or ISO form, times with an offset. R5b: these were part of
/// <see cref="FilterRules"/>, which only the filter bar is about.
/// </summary>
public static class ValueInput
{
    private static readonly CultureInfo German = ValueCulture.German;

    // German notation also with a decimal comma: the grid shows timestamps that way (14:30:05,123456), and users
    // type what they see (was rejected as "kein Zeitstempel" until 3.11.2).
    private static readonly string[] DateFormats =
    [
        "d.M.yyyy", "d.M.yyyy H:mm", "d.M.yyyy H:mm:ss", "d.M.yyyy H:mm:ss.FFFFFFF", "d.M.yyyy H:mm:ss,FFFFFFF",
        "yyyy-MM-dd", "yyyy-MM-dd H:mm", "yyyy-MM-dd H:mm:ss", "yyyy-MM-dd H:mm:ss.FFFFFFF",
        "yyyy-MM-ddTH:mm", "yyyy-MM-ddTH:mm:ss", "yyyy-MM-ddTH:mm:ss.FFFFFFF",
    ];

    /// <summary>Accepts "1234.5", "1.234,5" and "1234,5"; a comma switches to German notation.</summary>
    public static bool TryParseNumber(string text, out decimal value) =>
        text.Contains(',', StringComparison.Ordinal)
            ? decimal.TryParse(text.Trim(), NumberStyles.Number, German, out value)
            : decimal.TryParse(text.Trim(), NumberStyles.Number & ~NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out value);

    /// <summary>RAW values as hex digits (as the grid shows them), optionally with a 0x prefix.</summary>
    public static bool TryParseHex(string text, out byte[] value)
    {
        var hex = text.Trim();
        if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            hex = hex[2..];
        }

        try
        {
            value = Convert.FromHexString(hex);
            return value.Length > 0;
        }
        catch (FormatException)
        {
            value = [];
            return false;
        }
    }

    /// <summary>
    /// A time with an explicit offset at the end – <c>+02:00</c>, <c>-0530</c>, <c>+2</c> or <c>Z</c>, with or without a
    /// blank before it (ISO <c>2026-10-08T12:00:00+02:00</c>), the time as <see cref="TryParseDate"/> takes it. Region names
    /// (<c>Europe/Berlin</c>) are not taken. Shared by editing TIMESTAMP WITH TIME ZONE and the SQL editor's variables.
    /// </summary>
    /// <param name="offsetText">The offset as typed, for the message when it is out of range.</param>
    /// <returns>True: parsed. False: the offset is none (out of range). Null: no offset at the end – the text may still be a
    /// plain date (<c>2026-10-08</c>, whose "-08" only looks like one).</returns>
    public static bool? TryParseDateWithOffset(string text, out DateTimeOffset value, out string offsetText)
    {
        value = default;
        offsetText = "";
        if (OffsetSuffix.Match(text.Trim()) is not { Success: true } match || !TryParseDate(match.Groups["time"].Value, out var time, out _))
        {
            return null;
        }

        offsetText = match.Groups["offset"].Value;
        if (!TryOffset(offsetText, out var offset))
        {
            return false;
        }

        value = new DateTimeOffset(time, offset);
        return true;
    }

    private static readonly System.Text.RegularExpressions.Regex OffsetSuffix = new(
        @"^(?<time>.+?)\s*(?<offset>Z|[+-]\d{1,2}(?::?\d{2})?)$",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static bool TryOffset(string text, out TimeSpan offset)
    {
        offset = TimeSpan.Zero;
        if (text is "Z" or "z")
        {
            return true;
        }

        var digits = text[1..].Replace(":", "", StringComparison.Ordinal);
        var hours = int.Parse(digits.Length > 2 ? digits[..^2] : digits, CultureInfo.InvariantCulture);
        var minutes = digits.Length > 2 ? int.Parse(digits[^2..], CultureInfo.InvariantCulture) : 0;
        offset = new TimeSpan(hours, minutes, 0) * (text[0] == '-' ? -1 : 1);
        return minutes < 60 && offset >= TimeSpan.FromHours(-12) && offset <= TimeSpan.FromHours(14);
    }

    /// <param name="dateOnly">True if no time was given (equality then means "the whole day").</param>
    public static bool TryParseDate(string text, out DateTime value, out bool dateOnly)
    {
        var trimmed = text.Trim();
        var ok = DateTime.TryParseExact(trimmed, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out value);
        dateOnly = ok && !trimmed.Contains(':', StringComparison.Ordinal);
        return ok;
    }
}
