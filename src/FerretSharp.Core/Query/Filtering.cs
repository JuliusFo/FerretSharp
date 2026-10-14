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

/// <summary>Operator rules per column category; values are read by <see cref="ValueInput"/>.</summary>
public static class FilterRules
{
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
                ColumnCategory.Number when !ValueInput.TryParseNumber(value, out _) => TextFormat.Format(QueryText.NotANumber, value),
                ColumnCategory.Date or ColumnCategory.Timestamp or ColumnCategory.TimestampWithTimeZone when !ValueInput.TryParseDate(value, out _, out _) =>
                    TextFormat.Format(QueryText.NotADate, value),
                ColumnCategory.Raw when !ValueInput.TryParseHex(value, out _) => TextFormat.Format(QueryText.NotAHexValue, value),
                _ => null,
            };
            if (error is not null)
            {
                return error;
            }
        }

        return null;
    }
}
