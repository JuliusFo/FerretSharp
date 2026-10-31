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

    /// <summary>The group the row is in (WP-50); null for a row combined with AND.</summary>
    public FilterRowGroup? Group { get; set; }

    public string? Error { get; set; }

    public FilterCondition ToCondition() => new(Column, Op, FilterRules.ValueCount(Op) switch
    {
        0 => [],
        2 => [Value.Trim(), Value2.Trim()],
        -1 => Value.Split(ListSeparator, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries),
        _ => [Value.Trim()],
    }, Enabled) { Group = Group?.ToGroup() };

    /// <param name="group">The edited group the condition's group stands for; null for a row without one.</param>
    public static FilterRow From(FilterCondition condition, FilterRowGroup? group = null) => new()
    {
        Column = condition.Column,
        Op = condition.Op,
        Enabled = condition.Enabled,
        Group = group,
        Value = condition.Op == FilterOperator.In
            ? string.Join(ListSeparator + " ", condition.Values)
            : condition.Values.ElementAtOrDefault(0) ?? "",
        Value2 = condition.Values.ElementAtOrDefault(1) ?? "",
    };
}

/// <summary>A group of filter rows as edited in the filter bar (WP-50): OR or NOT, switched on or off as a whole.</summary>
public sealed class FilterRowGroup
{
    public int Id { get; init; }

    public FilterGroupKind Kind { get; set; }

    public bool Enabled { get; set; } = true;

    public FilterGroup ToGroup() => new(Id, Kind, Enabled);
}

/// <summary>An open table in the content area of a workspace.</summary>
public sealed class TableTab(Guid workspaceId, TableSummary table) : WorkspaceTab(workspaceId, "grid-"), IFormTab, ISynonymTab
{
    public TableSummary Table { get; } = table;

    public TabMode Mode { get; set; } = TabMode.Data;

    public SynonymInfo? DropRequested { get; set; }

    /// <summary>Rough scroll position: first visible row, only if it was loaded.</summary>
    public int? FirstVisibleRow { get; set; }

    /// <summary>
    /// Rows as edited in the filter bar (may be invalid or not yet applied): the rows without a group first, then the
    /// rows of each group in the order of <see cref="FilterGroups"/> – as the filter bar shows them, so the index of a
    /// row is that of its condition (errors). Change them through the methods below, which keep that order.
    /// </summary>
    public List<FilterRow> FilterRows { get; } = [];

    /// <summary>The groups of the filter bar in their order (WP-50), also those without a row yet.</summary>
    public List<FilterRowGroup> FilterGroups { get; } = [];

    /// <summary>The rows of a group, or those without one.</summary>
    public IEnumerable<FilterRow> RowsOf(FilterRowGroup? group) => FilterRows.Where(r => r.Group == group);

    /// <summary>The filter rows as edited, as conditions (with their groups).</summary>
    public IReadOnlyList<FilterCondition> EditedFilters() => FilterRows.Select(r => r.ToCondition()).ToList();

    /// <summary>
    /// Replaces the rows and groups of the filter bar (a preset, a link, a restored tab). The groups are numbered 1, 2, …
    /// anew: ids from outside can be anything (<see cref="Core.Query.FilterGroups.Normalize"/>).
    /// </summary>
    public void SetFilterRows(IEnumerable<FilterCondition> filters)
    {
        FilterRows.Clear();
        FilterGroups.Clear();
        var groups = new Dictionary<int, FilterRowGroup>();
        foreach (var filter in Core.Query.FilterGroups.Normalize(filters.ToList()))
        {
            FilterRowGroup? group = null;
            if (filter.Group is { } g && !groups.TryGetValue(g.Id, out group))
            {
                groups[g.Id] = group = new FilterRowGroup { Id = g.Id, Kind = g.Kind, Enabled = g.Enabled };
                FilterGroups.Add(group);
            }

            FilterRows.Add(FilterRow.From(filter, group));
        }

        OrderFilterRows();
    }

    public void ClearFilterRows()
    {
        FilterRows.Clear();
        FilterGroups.Clear();
    }

    /// <summary>Adds a row at the end of a group, or of the rows without one.</summary>
    public void AddFilterRow(FilterRow row, FilterRowGroup? group = null)
    {
        row.Group = group;
        FilterRows.Add(row);
        OrderFilterRows();
    }

    public void RemoveFilterRow(FilterRow row) => FilterRows.Remove(row);

    /// <summary>A new, empty group at the end, with the smallest free id.</summary>
    public FilterRowGroup AddFilterGroup(FilterGroupKind kind)
    {
        var group = new FilterRowGroup { Id = Core.Query.FilterGroups.FreeId(FilterGroups.Select(g => g.Id).ToHashSet()), Kind = kind };
        FilterGroups.Add(group);
        return group;
    }

    /// <summary>Moves a row to the end of a group, or out of its group (null); a row no longer in the bar stays out.</summary>
    public void MoveFilterRow(FilterRow row, FilterRowGroup? group)
    {
        if (FilterRows.Remove(row))
        {
            AddFilterRow(row, group);
        }
    }

    /// <summary>
    /// Ends a group: its rows stay, without a group (decision of the user, WP-50). The rows of a disabled group come
    /// disabled: they filtered nothing before.
    /// </summary>
    public void DissolveFilterGroup(FilterRowGroup group)
    {
        foreach (var row in RowsOf(group))
        {
            row.Group = null;
            row.Enabled &= group.Enabled;
        }

        FilterGroups.Remove(group);
        OrderFilterRows();
    }

    /// <summary>Rows without a group first, then group by group; a group only a row knows is added at the end.</summary>
    private void OrderFilterRows()
    {
        foreach (var group in FilterRows.Select(r => r.Group).OfType<FilterRowGroup>().Distinct().Where(g => !FilterGroups.Contains(g)).ToList())
        {
            FilterGroups.Add(group);
        }

        var ordered = RowsOf(null).Concat(FilterGroups.SelectMany(RowsOf)).ToList();
        FilterRows.Clear();
        FilterRows.AddRange(ordered);
    }

    /// <summary>Filters the grid currently shows.</summary>
    public IReadOnlyList<FilterCondition> AppliedFilters { get; set; } = [];

    /// <summary>
    /// The filter rows as edited, if all enabled ones are valid for <paramref name="table"/>; null otherwise. Each row gets
    /// its error (or none), so the filter bar shows what is wrong – applying, saving and updating a preset all check so.
    /// </summary>
    public IReadOnlyList<FilterCondition>? ValidFilters(TableDetails table)
    {
        var conditions = EditedFilters();
        var errors = QueryBuilder.Validate(table, conditions);
        for (var i = 0; i < FilterRows.Count; i++)
        {
            FilterRows[i].Error = errors.GetValueOrDefault(i);
        }

        return errors.Count == 0 ? conditions : null;
    }

    public IReadOnlyList<SortSpec> Sorts { get; set; } = [];

    /// <summary>The filter preset last applied in full (WP-37); the filter bar shows it and whether the rows changed since.</summary>
    public Guid? Preset { get; set; }

    /// <summary>
    /// Filter rows and sort order came from a link (WP-37) and were made for the sender's version of the table: fitted to
    /// this one when its structure first arrives (<see cref="UseStructure"/>), like a preset – rows on missing columns come
    /// disabled, sorts the table cannot do go. Not saved: a restored tab was fitted already.
    /// </summary>
    public bool FitToStructure { get; set; }

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

    public int ActiveFilterCount => Core.Query.FilterGroups.Active(AppliedFilters).Count();

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
    /// The structure changed in the database while uncommitted changes held the tab to the old one: commit or rollback
    /// rebuilds it (<see cref="WorkspaceEditing"/>). Not saved.
    /// </summary>
    public bool StructureStale { get; set; }

    /// <summary>
    /// A stale tab whose changes are gone – committed, rolled back, or discarded, undone, reverted (review 3.31.0, A30) –
    /// starts over with the new structure. True if it did.
    /// </summary>
    public bool DropStaleStructure()
    {
        if (!StructureStale || Changes is { HasChanges: true })
        {
            return false;
        }

        StructureStale = false;
        Changes = null; // the grid makes a tracker for the new structure
        StructureVersion++;
        return true;
    }

    /// <summary>
    /// The structure the tab's views work with, given the one just loaded (R4a): uncommitted changes address columns by
    /// position, so while there are any the tab keeps the structure they were made against. A tracker without changes made
    /// for another structure goes – the grid makes a new one.
    /// </summary>
    public TableDetails UseStructure(TableDetails loaded)
    {
        if (Changes is { } changes && !ReferenceEquals(changes.Table, loaded))
        {
            if (changes.HasChanges)
            {
                return Details = changes.Table;
            }

            Changes = null;
        }

        StructureStale = false;
        if (FitToStructure)
        {
            FitToStructure = false;
            var fitted = FilterPresets.Fit(loaded, AppliedFilters, Sorts);
            AppliedFilters = fitted.Filters;
            Sorts = fitted.Sorts ?? [];
            SetFilterRows(fitted.Filters);
        }

        return Details = loaded;
    }

    /// <summary>
    /// The open edit mode of the "Columns" view (WP-23): its draft and the failure of the last run. Kept here because DDL
    /// that changed the structure rebuilds the tab's views – the draft then reopens with what did not run yet. Not saved.
    /// </summary>
    public DesignerSession? Designer { get; set; }

    /// <summary>
    /// Another view asked for the edit mode at one of its tabs ("Edit" in the "Constraints" view): the "Columns" view opens
    /// it there and clears the request.
    /// </summary>
    public DesignerSection? OpenDesignerAt { get; set; }

    /// <summary>
    /// Short form of the active filters ("KUNDE_ID = 4711"), to tell several tabs of one table apart. With a C# model
    /// enum and bool values read as their members ("KUNDENART = Gewerbe").
    /// </summary>
    public string? FilterSummary(int maxLength = 40, TablePresentation? presentation = null)
    {
        var text = string.Join(", ", FilterTexts(presentation));
        return text.Length == 0 ? null : text.Length <= maxLength ? text : text[..(maxLength - 1)].TrimEnd() + "…";
    }

    /// <summary>The active filters, one text each; a group (WP-50) as one: <c>(A | B)</c>, <c>¬(A | B)</c>.</summary>
    public IEnumerable<string> FilterTexts(TablePresentation? presentation = null)
    {
        var filters = AppliedFilters;
        foreach (var term in Core.Query.FilterGroups.Terms(filters))
        {
            var texts = term.Indexes.Select(i => Text(filters[i])).ToList();
            yield return term.Group switch
            {
                null => texts[0],
                { Kind: FilterGroupKind.None } => $"¬({string.Join(" | ", texts)})",
                _ when texts.Count == 1 => texts[0],
                _ => $"({string.Join(" | ", texts)})",
            };
        }

        string Text(FilterCondition f) => FilterText.Row(f, presentation);
    }

    /// <param name="workspaceTabs">The tabs of the workspace in order, to save the origin as an index.</param>
    public override TabState ToState(IReadOnlyList<WorkspaceTab> workspaceTabs) => new(
        Table.Ref, Mode, EditedFilters(), AppliedFilters, Sorts, FirstVisibleRow)
    {
        PinnedColumns = PinnedColumns,
        Preset = Preset,
        OriginTab = BackTarget(workspaceTabs) is { } origin ? IndexOf(workspaceTabs, origin) : null,
        Form = FormTabs.Save(this),
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
            Preset = state.Preset,
        }.WithForm(state.Form);
        tab.SetFilterRows(state.FilterRows);
        return tab;
    }
}
