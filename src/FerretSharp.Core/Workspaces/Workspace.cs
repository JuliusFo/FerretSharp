using FerretSharp.Core.Query;
using FerretSharp.Core.Schema;

namespace FerretSharp.Core.Workspaces;

/// <summary>View of a table tab: the data grid or one of the detail views. <see cref="Structure"/> shows the columns.</summary>
public enum TabMode
{
    Data,
    Structure,
    Constraints,
    Indexes,
    Dependencies,

    /// <summary>The private and public synonyms on the object, to create and drop them (WP-68). Stored by name, so its place here is free.</summary>
    Synonyms,

    /// <summary>The object privileges on the object, to grant and revoke them (WP-71). Stored by name, so its place here is free.</summary>
    Grants,

    /// <summary>Diagram of the tables it references and that reference it (WP-46). Stored by name, so its place here is free.</summary>
    Relations,
    Ddl,

    /// <summary>Uses of the columns in the linked solution's code (WP-40).</summary>
    Usage,
}

/// <summary>
/// Persisted state of one table tab. <see cref="Table"/> is the real object; a synonym is resolved again from the
/// schema cache on restore.
/// </summary>
/// <param name="FilterRows">Filter rows as edited, including disabled and not yet applied ones.</param>
/// <param name="AppliedFilters">Filters the grid showed.</param>
/// <param name="FirstVisibleRow">Rough scroll position; only set for rows that had been loaded.</param>
public sealed record TabState(
    TableRef Table,
    TabMode Mode,
    IReadOnlyList<FilterCondition> FilterRows,
    IReadOnlyList<FilterCondition> AppliedFilters,
    IReadOnlyList<SortSpec> Sorts,
    int? FirstVisibleRow = null)
{
    /// <summary>Columns the user pinned to the left, in pin order; primary key columns are always pinned (<see cref="ColumnPinning"/>).</summary>
    public IReadOnlyList<string> PinnedColumns { get; init; } = [];

    /// <summary>
    /// The tab itself is pinned (WP-34): it stands at the left with the other pinned tabs and stays open when closing
    /// other tabs, the tabs to the right or all tabs. Not to be confused with <see cref="PinnedColumns"/>.
    /// </summary>
    public bool Pinned { get; init; }

    /// <summary>Index of the tab (in the same workspace) this one was opened from by an FK jump, for "Zurück"; null if none.</summary>
    public int? OriginTab { get; init; }

    /// <summary>
    /// Set for a LINQ console tab (WP-13); <see cref="Table"/> is then <see cref="LinqTabState.NoTable"/>. An older
    /// FerretSharp finds no such table and drops the tab, as it does with tables that are gone.
    /// </summary>
    public LinqTabState? Linq { get; init; }

    /// <summary>Set for a SQL editor tab (WP-17); <see cref="Table"/> is then <see cref="LinqTabState.NoTable"/> as well.</summary>
    public SqlTabState? Sql { get; init; }

    /// <summary>
    /// The filter preset last applied in full (WP-37), to show it in the filter bar and whether the rows changed since;
    /// null if none or the rows were cleared.
    /// </summary>
    public Guid? Preset { get; init; }

    /// <summary>The form beside the grid (WP-21; of a SQL or LINQ tab: beside its result, WP-56); null if it was never opened in the tab.</summary>
    public FormTabState? Form { get; init; }

    /// <summary>
    /// Result columns the user pinned in a SQL or LINQ tab (WP-73, <see cref="ResultPinning"/>); null if none. Not to be
    /// confused with <see cref="PinnedColumns"/> of a table tab.
    /// </summary>
    public IReadOnlyList<ResultColumnPin>? ResultPins { get; init; }

    /// <summary>Set for a PL/SQL tab (WP-28); <see cref="Table"/> is then <see cref="LinqTabState.NoTable"/> as well.</summary>
    public PlSqlTabState? PlSql { get; init; }

    /// <summary>Set for a record diagram tab (WP-60); <see cref="Table"/> is then <see cref="LinqTabState.NoTable"/> as well.</summary>
    public RecordTabState? Records { get; init; }

    /// <summary>
    /// The query built in the relationship view of a table tab (WP-69); null if none was started. Kept when the tab shows
    /// another view – a query often grows over several days.
    /// </summary>
    public Relations.QueryDesignState? Design { get; init; }

    /// <summary>The table the relationship view of a table tab shows the ways to (WP-74); null for its neighbours.</summary>
    public Schema.TableRef? PathTarget { get; init; }

    /// <summary>A LINQ console tab in the file format of table tabs.</summary>
    public static TabState OfLinq(LinqTabState linq) => new(LinqTabState.NoTable, TabMode.Data, [], [], []) { Linq = linq };

    /// <summary>A SQL editor tab in the file format of table tabs.</summary>
    public static TabState OfSql(SqlTabState sql) => new(LinqTabState.NoTable, TabMode.Data, [], [], []) { Sql = sql };

    /// <summary>A PL/SQL tab in the file format of table tabs.</summary>
    public static TabState OfPlSql(PlSqlTabState plSql) => new(LinqTabState.NoTable, TabMode.Data, [], [], []) { PlSql = plSql };

    /// <summary>A record diagram tab in the file format of table tabs.</summary>
    public static TabState OfRecords(RecordTabState records) => new(LinqTabState.NoTable, TabMode.Data, [], [], []) { Records = records };

    /// <summary>Set for the tab of the schema's synonyms (WP-68); <see cref="Table"/> is then <see cref="LinqTabState.NoTable"/> as well.</summary>
    public SynonymListTabState? SynonymList { get; init; }

    /// <summary>The tab of the schema's synonyms in the file format of table tabs.</summary>
    public static TabState OfSynonymList(SynonymListTabState list) => new(LinqTabState.NoTable, TabMode.Data, [], [], []) { SynonymList = list };

    /// <summary>Set for the tab of the schema's privileges (WP-71); <see cref="Table"/> is then <see cref="LinqTabState.NoTable"/> as well.</summary>
    public GrantListTabState? GrantList { get; init; }

    /// <summary>The tab of the schema's privileges in the file format of table tabs.</summary>
    public static TabState OfGrantList(GrantListTabState list) => new(LinqTabState.NoTable, TabMode.Data, [], [], []) { GrantList = list };

    /// <summary>
    /// Set for the tab of the workspace's data diff (WP-44); <see cref="Table"/> is then <see cref="LinqTabState.NoTable"/> as well.
    /// The start point and the tables live in <see cref="Workspace.DataDiff"/>, the results only in memory.
    /// </summary>
    public DataDiffTabState? DataDiff { get; init; }

    /// <summary>The tab of the workspace's data diff in the file format of table tabs.</summary>
    public static TabState OfDataDiff(DataDiffTabState diff) => new(LinqTabState.NoTable, TabMode.Data, [], [], []) { DataDiff = diff };

    /// <summary>
    /// Set for a table tab that shows its table as it was at an SCN (time travel, WP-78): read only, on the connection's read
    /// session. Null for a tab of now.
    /// </summary>
    public TimeTravelState? TimeTravel { get; init; }

    /// <summary>
    /// Set for a table tab that filters by a condition written in SQL instead of its filter bar (WP-79); null for the filter
    /// bar. <see cref="FilterRows"/> and <see cref="AppliedFilters"/> stay as they were: going back to the filter bar finds them.
    /// An older FerretSharp ignores it and shows the filter bar.
    /// </summary>
    public SqlConditionState? SqlCondition { get; init; }
}

/// <summary>The condition of a table tab in SQL mode (WP-79): as edited, and as the grid shows it.</summary>
/// <param name="Text">The condition as edited, without <c>WHERE</c>.</param>
/// <param name="Applied">The condition the grid shows; null while none was applied (the grid then shows the filter bar's rows).</param>
/// <param name="Origin">
/// The text the mode started with (the filter bar's filter as SQL, or empty): unchanged, going back to the filter bar loses nothing.
/// </param>
public sealed record SqlConditionState(string Text, string? Applied = null, string Origin = "")
{
    /// <summary>Back to the filter bar loses nothing: the text is still the one the mode started with, and so is what is applied.</summary>
    /// <remarks>Line ends do not count: Monaco writes the platform's into a text it is given.</remarks>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool Unchanged => ConditionHistoryStore.Same(Text, Origin) && (Applied is null || ConditionHistoryStore.Same(Applied, Origin));

    /// <summary>Text set to null in the file by hand becomes empty.</summary>
    public SqlConditionState Normalized() => this with { Text = Text ?? "", Origin = Origin ?? "" };
}

/// <summary>
/// The point a time travel tab shows (WP-78): the SCN, the time it was chosen for and the database it belongs to. Only numbers
/// and a name, no values.
/// </summary>
public sealed record TimeTravelState(long Scn, DateTimeOffset At, string Database)
{
    /// <summary>A point set to nonsense in the file by hand is dropped (the tab shows now then).</summary>
    public TimeTravelState? Normalized() => Scn <= 0 ? null : this with { Database = Database ?? "" };
}

/// <summary>The tab of a workspace's data diff (WP-44): only that it is there – what it compares is the workspace's, its results are not saved.</summary>
public sealed record DataDiffTabState;

/// <summary>The tab of the schema's private synonyms (WP-68): whether it shows only those whose target is not there.</summary>
public sealed record SynonymListTabState(bool OnlyBroken = false);

/// <summary>
/// The tab of the schema's object privileges (WP-71): the grantee it is filtered by (as in the dictionary; null for all) and
/// whether it shows the objects that grantee has no privilege on instead of the grantee's privileges.
/// </summary>
public sealed record GrantListTabState(string? Grantee = null, bool OnlyWithout = false);

/// <summary>
/// A record diagram tab (WP-60): the table of the start rows (the real object), their keys as text
/// (<see cref="Relations.RecordKeys"/>) and what was expanded since. The rows are read again when the tab is shown.
/// </summary>
/// <param name="RowIds">The keys are ROWIDs (a table without an enforced primary key).</param>
/// <param name="Depth">Levels the next expansion goes (WP-62, "Ebenen" in the diagram's header); each step keeps its own.</param>
public sealed record RecordTabState(
    string Owner, string Name, bool RowIds, IReadOnlyList<IReadOnlyList<string>> Keys, IReadOnlyList<Relations.RecordStep> Steps, int Depth = 1)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public TableRef Table => new(Owner, Name);
}

/// <summary>The form view of a tab (WP-21, WP-56): whether the panel is open, its width and "leere ausblenden".</summary>
/// <param name="Width">Width of the panel in CSS pixels; null for the default.</param>
public sealed record FormTabState(bool Open, int? Width = null, bool HideEmpty = false);

/// <summary>The views of a PL/SQL tab (WP-28). <see cref="Source"/> is a package's specification.</summary>
public enum PlSqlView
{
    Source,
    Body,
    Parameters,
    Errors,
    Dependencies,

    /// <summary>The synonyms on the unit (WP-68); not for triggers, nothing reaches a trigger by name.</summary>
    Synonyms,

    /// <summary>The object privileges on the unit (WP-71); not for triggers, they have none.</summary>
    Grants,
}

/// <summary>A PL/SQL tab: the unit (the real object; a synonym is resolved again on restore) and the view shown.</summary>
public sealed record PlSqlTabState(string Owner, string Name, PlSqlKind Kind, PlSqlView View)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public PlSqlRef Ref => new(Owner, Name, Kind);
}

/// <summary>A SQL editor tab: its title, the script and the bind variables with their types and values.</summary>
/// <param name="EditorHeight">Height of the editor in CSS pixels as the user dragged it, per tab; null for the default.</param>
public sealed record SqlTabState(string Title, string Text, IReadOnlyList<Query.SqlVariable> Variables, int? EditorHeight = null)
{
    /// <summary>The script file the tab is linked to (WP-77); null for a tab of its own.</summary>
    public Scripts.ScriptLink? Script { get; init; }
}

/// <summary>A LINQ console tab: its title, the code and the variables the code uses.</summary>
/// <param name="EditorHeight">Height of the query editor in CSS pixels as the user dragged it, per tab; null for the default.</param>
public sealed record LinqTabState(string Title, string Code, string Variables, int? EditorHeight = null)
{
    public static readonly TableRef NoTable = new("", "");
}

/// <summary>
/// A named working context on one connection (e.g. "Bug 3711") with its own tabs. At runtime every open workspace
/// has its own database session (<see cref="WorkspaceManager"/>); in v2 that session carries its own transaction.
/// </summary>
/// <param name="Order">Position in the workspace bar.</param>
/// <param name="LastActive">When the workspace was last activated; the most recent open one is active after connecting.</param>
public sealed record Workspace(Guid Id, Guid ConnectionId, string Name)
{
    public const int MaxNameLength = 40;

    public string Notes { get; init; } = "";

    public IReadOnlyList<TabState> Tabs { get; init; } = [];

    /// <summary>Index into <see cref="Tabs"/>; -1 if there is no tab.</summary>
    public int ActiveTabIndex { get; init; } = -1;

    public bool IsOpen { get; init; } = true;

    public int Order { get; init; }

    public DateTimeOffset LastActive { get; init; }

    /// <summary>The start point of the data diff (WP-44) and what it compares; null if none is set.</summary>
    public DataDiffState? DataDiff { get; init; }

    /// <summary>
    /// The workspace with what was set to null in its file by hand as empty: tabs that are null are left out, their lists
    /// and SQL variables become empty (R5b, review 3.31.0 B2: the stores normalized such nulls, the workspaces did not).
    /// </summary>
    public Workspace Normalized() => this with
    {
        Notes = Notes ?? "",
        Tabs = (Tabs ?? []).OfType<TabState>().Select(tab => tab with
        {
            FilterRows = (tab.FilterRows ?? []).OfType<FilterCondition>().ToList(),
            AppliedFilters = (tab.AppliedFilters ?? []).OfType<FilterCondition>().ToList(),
            Sorts = (tab.Sorts ?? []).OfType<SortSpec>().ToList(),
            PinnedColumns = (tab.PinnedColumns ?? []).OfType<string>().ToList(),
            ResultPins = tab.ResultPins is { } pins ? ResultPinning.Normalize(pins.OfType<ResultColumnPin>()) : null,
            Sql = tab.Sql is { } sql ? sql with { Text = sql.Text ?? "", Variables = (sql.Variables ?? []).OfType<SqlVariable>().ToList() } : null,
            Records = tab.Records is { } records ? records with
            {
                Keys = (records.Keys ?? []).Where(k => k is not null).Select(k => (IReadOnlyList<string>)k.OfType<string>().ToList()).ToList(),
                Steps = (records.Steps ?? []).Where(s => s is not null)
                    .Select(s => s with { Nodes = (s.Nodes ?? []).OfType<string>().ToList(), Depth = Relations.RecordDiagram.ClampDepth(s.Depth) }).ToList(),
                Depth = Relations.RecordDiagram.ClampDepth(records.Depth),
            } : null,
            Design = tab.Design?.Normalized(),
            PathTarget = tab.PathTarget is { Owner: not null, Name: not null } target ? target : null,
            TimeTravel = tab.TimeTravel?.Normalized(),
            SqlCondition = tab.TimeTravel is null ? tab.SqlCondition?.Normalized() : null, // a time travel tab has no SQL mode (decision of the user)
        }).ToList(),
        DataDiff = DataDiff?.Normalized(),
    };

    /// <summary>
    /// <paramref name="name"/> as a tab title: trimmed and, if longer than <see cref="MaxNameLength"/>, cut with "…" – never
    /// inside a surrogate pair (review WP-77: a script's file name may be 255 characters).
    /// </summary>
    public static string FitName(string name)
    {
        var trimmed = name.Trim();
        if (trimmed.Length <= MaxNameLength)
        {
            return trimmed;
        }

        var cut = MaxNameLength - 1;
        if (char.IsHighSurrogate(trimmed[cut - 1]))
        {
            cut--;
        }

        return trimmed[..cut].TrimEnd() + "…";
    }

    /// <summary>Trimmed name, or null if it is empty or longer than <see cref="MaxNameLength"/>.</summary>
    public static string? NormalizeName(string? name)
    {
        var trimmed = name?.Trim() ?? "";
        return trimmed.Length is > 0 and <= MaxNameLength ? trimmed : null;
    }

    /// <summary>
    /// "Workspace 2", "SQL 3": the prefix with the smallest number from 1 on that none of <paramref name="taken"/> has
    /// (ignoring case). After "Workspace 1" was renamed, the next new one is "Workspace 1" again.
    /// </summary>
    public static string FirstFreeName(string prefix, IEnumerable<string> taken)
    {
        var used = taken.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var n = 1;
        while (used.Contains(prefix + n))
        {
            n++;
        }

        return prefix + n;
    }
}

/// <summary>
/// The start point of a workspace's data diff (WP-44): the SCN and when it was set, and the tables it compares – each with the
/// filter it was added with (empty for the whole table). Kept with the workspace, so it survives a restart; no row values
/// (the filters with their values, as in the tabs).
/// </summary>
/// <param name="Scn">
/// The database's SCN at the start (<see cref="Data.IDataAccess.CurrentPointAsync"/>), or at a time chosen before now
/// (<see cref="Data.IDataAccess.PointBackAsync"/>, WP-78).
/// </param>
/// <param name="StartedAt">When the start point lies: the click, or the time chosen.</param>
public sealed record DataDiffState(long Scn, DateTimeOffset StartedAt, IReadOnlyList<DataDiffEntry> Entries)
{
    /// <summary>
    /// The database the SCN belongs to (<see cref="Data.DatabasePoint.Database"/>, WP-78); null in files of older versions,
    /// which are not checked.
    /// </summary>
    public string? Database { get; init; }

    /// <summary>
    /// Entries set to null in the file by hand are left out, their filters empty; a table twice keeps its first entry (the tab
    /// keys its list by table – review of WP-44).
    /// </summary>
    public DataDiffState? Normalized() =>
        Scn <= 0 ? null : this with
        {
            Entries = (Entries ?? []).Where(e => e is { Table: { Owner: not null, Name: not null } })
                .DistinctBy(e => e.Table)
                .Select(e => e with { Filters = (e.Filters ?? []).OfType<FilterCondition>().ToList() }).ToList(),
        };

    /// <summary>The entry of <paramref name="table"/> with <paramref name="filters"/>: added, or its filters replaced.</summary>
    public DataDiffState WithEntry(TableRef table, IReadOnlyList<FilterCondition> filters)
    {
        var entries = Entries.ToList();
        var index = entries.FindIndex(e => e.Table == table);
        var entry = new DataDiffEntry(table, filters);
        if (index < 0)
        {
            entries.Add(entry);
        }
        else
        {
            entries[index] = entry;
        }

        return this with { Entries = entries };
    }

    public DataDiffState WithoutEntry(TableRef table) => this with { Entries = Entries.Where(e => e.Table != table).ToList() };
}

/// <summary>A table of a data diff (WP-44) with the filter it compares (empty: the whole table).</summary>
public sealed record DataDiffEntry(TableRef Table, IReadOnlyList<FilterCondition> Filters);
