using FerretSharp.Core.ClrModel;
using FerretSharp.Core.Connections;
using FerretSharp.Core.Data;
using FerretSharp.Core.Query;
using FerretSharp.Core.Schema;
using FerretSharp.Core.Workspaces;

namespace FerretSharp.UI.State;

/// <summary>One editable row of the filter bar.</summary>
public sealed class FilterRow
{
    /// <summary>Separator for IN lists; a comma would clash with German decimal commas.</summary>
    public const char ListSeparator = FilterCondition.ListSeparator;

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
public sealed class TableTab(Guid workspaceId, TableSummary table) : WorkspaceTab(workspaceId, "grid-")
{
    public TableSummary Table { get; } = table;

    public TabMode Mode { get; set; } = TabMode.Data;

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

    /// <summary>Snapshot the shown data comes from (read-only profiles): the one of the first page; null without one.</summary>
    public DateTimeOffset? DataAsOf { get; set; }

    /// <summary>
    /// A later page came from another snapshot than the first: another tab of the workspace started a new one meanwhile
    /// (the session has one snapshot for all its tabs). Rows may then repeat or be missing at the page border.
    /// </summary>
    public bool SnapshotMoved { get; set; }

    /// <summary>
    /// Edits of this tab (v2); null while the table cannot be edited (read-only connection, view, no row key) or
    /// its structure is not loaded yet. Not saved with the workspace: uncommitted work is confirmed before leaving.
    /// </summary>
    public ChangeTracker? Changes { get; set; }

    /// <summary>A row to focus once the grid has loaded it (change overview, WP-30); not saved.</summary>
    public RowChange? FocusRequest { get; set; }

    public long? TotalCount { get; set; }

    public string? Error { get; set; }

    /// <summary>The database error behind <see cref="Error"/>, for the details dialog; null for validation errors.</summary>
    public DatabaseException? Failure { get; set; }

    public int ActiveFilterCount => AppliedFilters.Count(f => f.Enabled);

    /// <summary>The tab this one was opened from by an FK jump ("Back"); it may have been closed since.</summary>
    public TableTab? Origin { get; set; }

    /// <summary>The tab the user came back from with "Back" ("Forward"); not saved.</summary>
    public TableTab? Forward { get; set; }

    /// <summary>
    /// Where "Back" leads: the origin tab, or – if that was closed – the nearest open tab further back along the
    /// chain of jumps. Null if there is none.
    /// </summary>
    public TableTab? BackTarget(IReadOnlyCollection<WorkspaceTab> open)
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

    public TableTab? ForwardTarget(IReadOnlyCollection<WorkspaceTab> open) => Forward is { } tab && open.Contains(tab) ? tab : null;

    /// <summary>The form beside the grid is open (WP-21); saved with the workspace.</summary>
    public bool FormOpen { get; set; }

    /// <summary>Width of the form in CSS pixels; null for the default.</summary>
    public int? FormWidth { get; set; }

    /// <summary>"Leere ausblenden" in the form, per tab (decision of the user).</summary>
    public bool FormHideEmpty { get; set; }

    /// <summary>The table's structure once the tab has loaded it (for labels outside the tab, e.g. its header).</summary>
    public TableDetails? Details { get; set; }

    /// <summary>
    /// Counts up when the table's structure changed in the database (WP-22: DDL, "Schema neu laden"): the tab's view is
    /// keyed on it and builds grid, form and detail views again from the new structure.
    /// </summary>
    public int StructureVersion { get; set; }

    /// <summary>
    /// Short form of the active filters ("KUNDE_ID = 4711"), to tell several tabs of one table apart. With a C# model
    /// enum and bool values read as their members ("KUNDENART = Gewerbe").
    /// </summary>
    public string? FilterSummary(int maxLength = 40, TablePresentation? presentation = null)
    {
        var text = string.Join(", ", FilterTexts(presentation));
        return text.Length == 0 ? null : text.Length <= maxLength ? text : text[..(maxLength - 1)].TrimEnd() + "…";
    }

    /// <summary>The active filters, one text each.</summary>
    public IEnumerable<string> FilterTexts(TablePresentation? presentation = null) => AppliedFilters
        .Where(f => f.Enabled)
        .Select(f => $"{f.Column} {OperatorLabels.Label(f.Op)} {string.Join("; ", f.Values.Select(v => presentation?.FilterValueText(f.Column, v) ?? v))}".TrimEnd());

    /// <param name="workspaceTabs">The tabs of the workspace in order, to save the origin as an index.</param>
    public override TabState ToState(IReadOnlyList<WorkspaceTab> workspaceTabs) => new(
        Table.Ref, Mode, FilterRows.Select(r => r.ToCondition()).ToList(), AppliedFilters, Sorts, FirstVisibleRow)
    {
        PinnedColumns = PinnedColumns,
        OriginTab = BackTarget(workspaceTabs) is { } origin ? IndexOf(workspaceTabs, origin) : null,
        Form = FormOpen || FormWidth is not null || FormHideEmpty ? new FormTabState(FormOpen, FormWidth, FormHideEmpty) : null,
    };

    private static int IndexOf(IReadOnlyList<WorkspaceTab> tabs, WorkspaceTab tab)
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
            FormOpen = state.Form?.Open ?? false,
            FormWidth = state.Form?.Width,
            FormHideEmpty = state.Form?.HideEmpty ?? false,
        };
        tab.FilterRows.AddRange(state.FilterRows.Select(FilterRow.From));
        return tab;
    }
}
