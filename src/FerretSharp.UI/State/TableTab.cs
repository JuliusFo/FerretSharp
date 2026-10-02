using FerretSharp.Core.Connections;
using FerretSharp.Core.Query;
using FerretSharp.Core.Schema;
using FerretSharp.Core.Workspaces;

namespace FerretSharp.UI.State;

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

/// <summary>An open table in the content area of a workspace.</summary>
public sealed class TableTab(Guid workspaceId, TableSummary table)
{
    public string Id { get; } = "grid-" + Guid.NewGuid().ToString("N");

    /// <summary>The workspace whose session runs this tab's queries.</summary>
    public Guid WorkspaceId { get; } = workspaceId;

    public TableSummary Table { get; } = table;

    public TabMode Mode { get; set; } = TabMode.Data;

    /// <summary>
    /// Set once the tab has been shown. Restored tabs are mounted (and query) only when first shown, so reopening
    /// a workspace with many tabs does not fire all their queries at once.
    /// </summary>
    public bool Visited { get; set; }

    /// <summary>Rough scroll position: first visible row, only if it was loaded.</summary>
    public int? FirstVisibleRow { get; set; }

    /// <summary>Rows as edited in the filter bar (may be invalid or not yet applied).</summary>
    public List<FilterRow> FilterRows { get; } = [];

    /// <summary>Filters the grid currently shows.</summary>
    public IReadOnlyList<FilterCondition> AppliedFilters { get; set; } = [];

    public IReadOnlyList<SortSpec> Sorts { get; set; } = [];

    /// <summary>Columns the user pinned to the left, in pin order (without the always pinned primary key).</summary>
    public IReadOnlyList<string> PinnedColumns { get; set; } = [];

    public int RowsLoaded { get; set; }

    public bool AllRowsLoaded { get; set; }

    public TimeSpan? LastQueryTime { get; set; }

    public long? TotalCount { get; set; }

    public string? Error { get; set; }

    /// <summary>The database error behind <see cref="Error"/>, for the details dialog; null for validation errors.</summary>
    public DatabaseException? Failure { get; set; }

    public int ActiveFilterCount => AppliedFilters.Count(f => f.Enabled);

    public TabState ToState() => new(
        Table.Ref, Mode, FilterRows.Select(r => r.ToCondition()).ToList(), AppliedFilters, Sorts, FirstVisibleRow)
    {
        PinnedColumns = PinnedColumns,
    };

    public static TableTab Restore(Guid workspaceId, TableSummary table, TabState state)
    {
        var tab = new TableTab(workspaceId, table)
        {
            Mode = state.Mode,
            AppliedFilters = state.AppliedFilters,
            Sorts = state.Sorts,
            FirstVisibleRow = state.FirstVisibleRow,
            PinnedColumns = state.PinnedColumns,
        };
        tab.FilterRows.AddRange(state.FilterRows.Select(FilterRow.From));
        return tab;
    }
}
