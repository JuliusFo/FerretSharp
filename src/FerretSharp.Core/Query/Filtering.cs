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

    /// <summary>
    /// The group the row belongs to (WP-50); null for a row combined with AND like before. Every row of a group carries
    /// the same group, so the list stays flat and each consumer (files, presets, links) takes the groups along.
    /// </summary>
    public FilterGroup? Group { get; init; }

    public static FilterCondition Of(string column, FilterOperator op, params string[] values) => new(column, op, values);
}

/// <summary>How the rows of a filter group combine (WP-50); groups combine with each other and the other rows by AND.</summary>
public enum FilterGroupKind
{
    /// <summary>At least one row applies: <c>(a OR b)</c>.</summary>
    Any,

    /// <summary>No row applies: <c>!(a || b)</c> as in C# – a row whose condition is unknown because of NULL counts as "does not apply".</summary>
    None,
}

/// <summary>A group of filter rows (WP-50). A disabled group is ignored as a whole; its rows keep their own switches.</summary>
/// <param name="Id">Tells the groups of one filter list apart; only unique within that list.</param>
public sealed record FilterGroup(int Id, FilterGroupKind Kind, bool Enabled = true);

/// <summary>
/// What a filter list means (WP-50): the rows without a group, each its own term, and the groups – all combined by AND.
/// A group's kind and switch come from its first row (a list read from outside may disagree), a group without an enabled
/// row filters nothing, like a disabled row.
/// </summary>
public static class FilterGroups
{
    /// <summary>One AND term: a row without a group, or a group with its enabled rows (indexes into the list).</summary>
    public sealed record Term(FilterGroup? Group, IReadOnlyList<int> Indexes);

    /// <summary>The groups of the list by id, as their first row has them.</summary>
    public static IReadOnlyDictionary<int, FilterGroup> Of(IReadOnlyList<FilterCondition> filters)
    {
        var groups = new Dictionary<int, FilterGroup>();
        foreach (var filter in filters)
        {
            if (filter.Group is { } group)
            {
                groups.TryAdd(group.Id, group);
            }
        }

        return groups;
    }

    /// <summary>Whether the row filters: it is enabled, and so is its group.</summary>
    public static bool IsActive(FilterCondition filter, IReadOnlyDictionary<int, FilterGroup> groups) =>
        filter.Enabled && (filter.Group is not { } group || groups.GetValueOrDefault(group.Id, group).Enabled);

    /// <summary>The rows that filter, in list order.</summary>
    public static IEnumerable<FilterCondition> Active(IReadOnlyList<FilterCondition> filters)
    {
        var groups = Of(filters);
        return filters.Where(f => IsActive(f, groups));
    }

    /// <summary>The AND terms of the list, in the order of their first row; groups without an active row are left out.</summary>
    public static IReadOnlyList<Term> Terms(IReadOnlyList<FilterCondition> filters)
    {
        var groups = Of(filters);
        var terms = new List<(FilterGroup? Group, List<int> Indexes)>();
        var byGroup = new Dictionary<int, List<int>>();
        for (var i = 0; i < filters.Count; i++)
        {
            var filter = filters[i];
            if (!IsActive(filter, groups))
            {
                continue;
            }

            if (filter.Group is not { } group)
            {
                terms.Add((null, [i]));
            }
            else if (byGroup.TryGetValue(group.Id, out var indexes))
            {
                indexes.Add(i);
            }
            else
            {
                byGroup[group.Id] = indexes = [i];
                terms.Add((groups[group.Id], indexes));
            }
        }

        return terms.Select(t => new Term(t.Group, t.Indexes)).ToList();
    }

    /// <summary>
    /// <paramref name="added"/> with new group ids, so its groups stay apart from those of <paramref name="existing"/>
    /// when both go into one list (a preset applied after the current rows).
    /// </summary>
    public static IReadOnlyList<FilterCondition> Renumber(IReadOnlyList<FilterCondition> existing, IReadOnlyList<FilterCondition> added)
    {
        var next = NextId(existing);
        var map = new Dictionary<int, FilterGroup>();
        return added.Select(f =>
        {
            if (f.Group is not { } group)
            {
                return f;
            }

            if (!map.TryGetValue(group.Id, out var renumbered))
            {
                map[group.Id] = renumbered = group with { Id = next++ };
            }

            return f with { Group = renumbered };
        }).ToList();
    }

    /// <summary>An id no group of the list uses.</summary>
    public static int NextId(IEnumerable<FilterCondition> filters) =>
        filters.Select(f => f.Group?.Id ?? 0).DefaultIfEmpty(0).Max() + 1;
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
