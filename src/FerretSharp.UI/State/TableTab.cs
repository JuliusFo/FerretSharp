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

    /// <summary>Snapshot the shown data comes from (read-only profiles); null without one.</summary>
    public DateTimeOffset? DataAsOf { get; set; }

    public long? TotalCount { get; set; }

    public string? Error { get; set; }

    /// <summary>The database error behind <see cref="Error"/>, for the details dialog; null for validation errors.</summary>
    public DatabaseException? Failure { get; set; }

    public int ActiveFilterCount => AppliedFilters.Count(f => f.Enabled);

    /// <summary>The tab this one was opened from by an FK jump ("Zurück"); it may have been closed since.</summary>
    public TableTab? Origin { get; set; }

    /// <summary>The tab the user came back from with "Zurück" ("Vor"); not saved.</summary>
    public TableTab? Forward { get; set; }

    /// <summary>
    /// Where "Zurück" leads: the origin tab, or – if that was closed – the nearest open tab further back along the
    /// chain of jumps. Null if there is none.
    /// </summary>
    public TableTab? BackTarget(IReadOnlyCollection<TableTab> open)
    {
        var seen = new HashSet<TableTab> { this };
        for (var tab = Origin; tab is not null && seen.Add(tab); tab = tab.Origin)
        {
            if (open.Contains(tab))
            {
                return tab;
            }
        }

        return null;
    }

    public TableTab? ForwardTarget(IReadOnlyCollection<TableTab> open) => Forward is { } tab && open.Contains(tab) ? tab : null;

    /// <summary>Short form of the active filters ("KUNDE_ID = 4711"), to tell several tabs of one table apart.</summary>
    public string? FilterSummary(int maxLength = 40)
    {
        var text = string.Join(", ", AppliedFilters
            .Where(f => f.Enabled)
            .Select(f => $"{f.Column} {OperatorLabels.Label(f.Op)} {string.Join("; ", f.Values)}".TrimEnd()));
        return text.Length == 0 ? null : text.Length <= maxLength ? text : text[..(maxLength - 1)].TrimEnd() + "…";
    }

    /// <param name="workspaceTabs">The tabs of the workspace in order, to save the origin as an index.</param>
    public TabState ToState(IReadOnlyList<TableTab> workspaceTabs) => new(
        Table.Ref, Mode, FilterRows.Select(r => r.ToCondition()).ToList(), AppliedFilters, Sorts, FirstVisibleRow)
    {
        PinnedColumns = PinnedColumns,
        OriginTab = BackTarget(workspaceTabs) is { } origin ? IndexOf(workspaceTabs, origin) : null,
    };

    private static int IndexOf(IReadOnlyList<TableTab> tabs, TableTab tab)
    {
        for (var i = 0; i < tabs.Count; i++)
        {
            if (tabs[i] == tab)
            {
                return i;
            }
        }

        return -1;
    }

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
