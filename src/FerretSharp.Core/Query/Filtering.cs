using System.Globalization;
using FerretSharp.Core.Resources;
using FerretSharp.Core.Schema;

namespace FerretSharp.Core.Query;

public enum FilterOperator
{
    Equals,
    NotEquals,
    Contains,
    StartsWith,
    EndsWith,
    Gt,
    Gte,
    Lt,
    Lte,
    Between,
    In,
    IsNull,
    IsNotNull,
}

/// <summary>
/// One filter row of the filter bar. <paramref name="Values"/> holds the text as typed (German or ISO formats);
/// count by operator: 0 (IS [NOT] NULL), 2 (BETWEEN), n (IN), otherwise 1. Disabled rows are kept but ignored.
/// </summary>
public sealed record FilterCondition(string Column, FilterOperator Op, IReadOnlyList<string> Values, bool Enabled = true)
{
    /// <summary>Separates the values of an IN list in the filter bar; a comma would clash with German decimal commas.</summary>
    public const char ListSeparator = ';';

    public static FilterCondition Of(string column, FilterOperator op, params string[] values) => new(column, op, values);
}

public sealed record SortSpec(string Column, bool Descending);

public sealed record PageSpec(int Offset, int Limit);

/// <summary>Operator rules per column category and value parsing.</summary>
public static class FilterRules
{
    private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");

    private static readonly FilterOperator[] Comparable =
    [
        FilterOperator.Equals, FilterOperator.NotEquals, FilterOperator.Gt, FilterOperator.Gte, FilterOperator.Lt,
        FilterOperator.Lte, FilterOperator.Between, FilterOperator.In, FilterOperator.IsNull, FilterOperator.IsNotNull,
    ];

    private static readonly FilterOperator[] TextOperators =
    [
        FilterOperator.Contains, FilterOperator.Equals, FilterOperator.NotEquals, FilterOperator.StartsWith, FilterOperator.EndsWith,
        FilterOperator.In, FilterOperator.Gt, FilterOperator.Gte, FilterOperator.Lt, FilterOperator.Lte, FilterOperator.Between,
        FilterOperator.IsNull, FilterOperator.IsNotNull,
    ];

    private static readonly FilterOperator[] LobOperators =
        [FilterOperator.Contains, FilterOperator.StartsWith, FilterOperator.EndsWith, FilterOperator.IsNull, FilterOperator.IsNotNull];

    private static readonly FilterOperator[] RawOperators =
        [FilterOperator.Equals, FilterOperator.NotEquals, FilterOperator.In, FilterOperator.IsNull, FilterOperator.IsNotNull];

    private static readonly FilterOperator[] NullOnly = [FilterOperator.IsNull, FilterOperator.IsNotNull];

    // German notation also with a decimal comma: the grid shows timestamps that way (14:30:05,123456), and users
    // type what they see (was rejected as "kein Zeitstempel" until 3.11.2).
    private static readonly string[] DateFormats =
    [
        "d.M.yyyy", "d.M.yyyy H:mm", "d.M.yyyy H:mm:ss", "d.M.yyyy H:mm:ss.FFFFFFF", "d.M.yyyy H:mm:ss,FFFFFFF",
        "yyyy-MM-dd", "yyyy-MM-dd H:mm", "yyyy-MM-dd H:mm:ss", "yyyy-MM-dd H:mm:ss.FFFFFFF",
        "yyyy-MM-ddTH:mm", "yyyy-MM-ddTH:mm:ss", "yyyy-MM-ddTH:mm:ss.FFFFFFF",
    ];

    /// <summary>Operators offered for a column; the first one is the default for a new filter row.</summary>
    public static IReadOnlyList<FilterOperator> OperatorsFor(ColumnCategory category) => category switch
    {
        ColumnCategory.Text => TextOperators,
        ColumnCategory.Number or ColumnCategory.Date or ColumnCategory.Timestamp or ColumnCategory.TimestampWithTimeZone => Comparable,
        ColumnCategory.Clob => LobOperators,
        ColumnCategory.Raw => RawOperators,
        _ => NullOnly,
    };

    public static int ValueCount(FilterOperator op) => op switch
    {
        FilterOperator.IsNull or FilterOperator.IsNotNull => 0,
        FilterOperator.Between => 2,
        FilterOperator.In => -1, // one or more
        _ => 1,
    };

    /// <returns>A user-facing error, or null if the condition can be turned into SQL.</returns>
    public static string? Validate(ColumnInfo column, FilterCondition filter)
    {
        var category = ColumnCategories.Of(column);
        if (!OperatorsFor(category).Contains(filter.Op))
        {
            return TextFormat.Format(QueryText.FilterOperatorNotAvailable, column.DisplayType);
        }

        var expected = ValueCount(filter.Op);
        var values = filter.Values;
        if (expected == 0)
        {
            return null;
        }

        if (expected > 0 && values.Count != expected || expected < 0 && values.Count == 0)
        {
            return expected == 2 ? QueryText.FilterTwoValuesNeeded : QueryText.FilterValueMissing;
        }

        foreach (var value in values)
        {
            if (value.Length == 0)
            {
                return category == ColumnCategory.Text && filter.Op is FilterOperator.Equals or FilterOperator.NotEquals
                    ? TextFormat.Format(QueryText.FilterEmptyTextIsNull, OperatorLabels.Label(FilterOperator.IsNull))
                    : QueryText.FilterValueMissing;
            }

            var error = category switch
            {
                ColumnCategory.Number when !TryParseNumber(value, out _) => TextFormat.Format(QueryText.NotANumber, value),
                ColumnCategory.Date or ColumnCategory.Timestamp or ColumnCategory.TimestampWithTimeZone when !TryParseDate(value, out _, out _) =>
                    TextFormat.Format(QueryText.NotADate, value),
                ColumnCategory.Raw when !TryParseHex(value, out _) => TextFormat.Format(QueryText.NotAHexValue, value),
                _ => null,
            };
            if (error is not null)
            {
                return error;
            }
        }

        return null;
    }

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
