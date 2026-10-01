using FerretSharp.Core.Query;
using FerretSharp.Core.Schema;

namespace FerretSharp.UI.State;

public enum TabMode { Data, Structure }

/// <summary>One editable row of the filter bar.</summary>
public sealed class FilterRow
{
    /// <summary>Separator for IN lists; a comma would clash with German decimal commas.</summary>
    public const char ListSeparator = ';';

    public string Column { get; set; } = "";

    public FilterOperator Op { get; set; }

    public string Value { get; set; } = "";

    /// <summary>Upper bound for BETWEEN.</summary>
    public string Value2 { get; set; } = "";

    public bool Enabled { get; set; } = true;

    public string? Error { get; set; }

    public FilterCondition ToCondition() => new(Column, Op, FilterRules.ValueCount(Op) switch
    {
        0 => [],
        2 => [Value.Trim(), Value2.Trim()],
        -1 => Value.Split(ListSeparator, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries),
        _ => [Value.Trim()],
    }, Enabled);

    public static FilterRow From(FilterCondition condition) => new()
    {
        Column = condition.Column,
        Op = condition.Op,
        Enabled = condition.Enabled,
        Value = condition.Op == FilterOperator.In
            ? string.Join(ListSeparator + " ", condition.Values)
            : condition.Values.ElementAtOrDefault(0) ?? "",
        Value2 = condition.Values.ElementAtOrDefault(1) ?? "",
    };
}

/// <summary>An open table in the content area (later part of a workspace, WP-05).</summary>
public sealed class TableTab(TableSummary table)
{
    public string Id { get; } = "grid-" + Guid.NewGuid().ToString("N");

    public TableSummary Table { get; } = table;

    public TabMode Mode { get; set; } = TabMode.Data;

    /// <summary>Rows as edited in the filter bar (may be invalid or not yet applied).</summary>
    public List<FilterRow> FilterRows { get; } = [];

    /// <summary>Filters the grid currently shows.</summary>
    public IReadOnlyList<FilterCondition> AppliedFilters { get; set; } = [];

    public IReadOnlyList<SortSpec> Sorts { get; set; } = [];

    public int RowsLoaded { get; set; }

    public bool AllRowsLoaded { get; set; }

    public TimeSpan? LastQueryTime { get; set; }

    public long? TotalCount { get; set; }

    public string? Error { get; set; }

    public int ActiveFilterCount => AppliedFilters.Count(f => f.Enabled);
}
