using System.Globalization;
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

    private static readonly FilterOperator[] NullOnly = [FilterOperator.IsNull, FilterOperator.IsNotNull];

    private static readonly string[] DateFormats =
    [
        "d.M.yyyy", "d.M.yyyy H:mm", "d.M.yyyy H:mm:ss", "d.M.yyyy H:mm:ss.FFFFFFF",
        "yyyy-MM-dd", "yyyy-MM-dd H:mm", "yyyy-MM-dd H:mm:ss", "yyyy-MM-dd H:mm:ss.FFFFFFF",
        "yyyy-MM-ddTH:mm", "yyyy-MM-ddTH:mm:ss", "yyyy-MM-ddTH:mm:ss.FFFFFFF",
    ];

    /// <summary>Operators offered for a column; the first one is the default for a new filter row.</summary>
    public static IReadOnlyList<FilterOperator> OperatorsFor(ColumnCategory category) => category switch
    {
        ColumnCategory.Text => TextOperators,
        ColumnCategory.Number or ColumnCategory.Date or ColumnCategory.Timestamp or ColumnCategory.TimestampWithTimeZone => Comparable,
        ColumnCategory.Clob => LobOperators,
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
            return $"Operator nicht möglich für {column.DisplayType}.";
        }

        var expected = ValueCount(filter.Op);
        var values = filter.Values;
        if (expected == 0)
        {
            return null;
        }

        if (expected > 0 && values.Count != expected || expected < 0 && values.Count == 0)
        {
            return expected == 2 ? "Zwei Werte nötig (von … bis)." : "Wert fehlt.";
        }

        foreach (var value in values)
        {
            if (value.Length == 0)
            {
                return category == ColumnCategory.Text && filter.Op is FilterOperator.Equals or FilterOperator.NotEquals
                    ? "Leerer Text ist in Oracle NULL – „ist NULL“ verwenden."
                    : "Wert fehlt.";
            }

            var error = category switch
            {
                ColumnCategory.Number when !TryParseNumber(value, out _) => $"„{value}“ ist keine Zahl.",
                ColumnCategory.Date or ColumnCategory.Timestamp or ColumnCategory.TimestampWithTimeZone when !TryParseDate(value, out _, out _) =>
                    $"„{value}“ ist kein Datum (TT.MM.JJJJ [hh:mm[:ss]]).",
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

    /// <param name="dateOnly">True if no time was given (equality then means "the whole day").</param>
    public static bool TryParseDate(string text, out DateTime value, out bool dateOnly)
    {
        var trimmed = text.Trim();
        var ok = DateTime.TryParseExact(trimmed, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out value);
        dateOnly = ok && !trimmed.Contains(':', StringComparison.Ordinal);
        return ok;
    }
}
