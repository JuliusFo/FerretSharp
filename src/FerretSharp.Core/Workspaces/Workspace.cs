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
    Ddl,
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

    /// <summary>Index of the tab (in the same workspace) this one was opened from by an FK jump, for "Zurück"; null if none.</summary>
    public int? OriginTab { get; init; }

    /// <summary>
    /// Set for a LINQ console tab (WP-13); <see cref="Table"/> is then <see cref="LinqTabState.NoTable"/>. An older
    /// FerretSharp finds no such table and drops the tab, as it does with tables that are gone.
    /// </summary>
    public LinqTabState? Linq { get; init; }

    /// <summary>Set for a SQL editor tab (WP-17); <see cref="Table"/> is then <see cref="LinqTabState.NoTable"/> as well.</summary>
    public SqlTabState? Sql { get; init; }

    /// <summary>The form beside the grid (WP-21); null if it was never opened in the tab.</summary>
    public FormTabState? Form { get; init; }

    /// <summary>A LINQ console tab in the file format of table tabs.</summary>
    public static TabState OfLinq(LinqTabState linq) => new(LinqTabState.NoTable, TabMode.Data, [], [], []) { Linq = linq };

    /// <summary>A SQL editor tab in the file format of table tabs.</summary>
    public static TabState OfSql(SqlTabState sql) => new(LinqTabState.NoTable, TabMode.Data, [], [], []) { Sql = sql };
}

/// <summary>The form view of a table tab (WP-21): whether the panel is open, its width and "leere ausblenden".</summary>
/// <param name="Width">Width of the panel in CSS pixels; null for the default.</param>
public sealed record FormTabState(bool Open, int? Width = null, bool HideEmpty = false);

/// <summary>A SQL editor tab: its title, the script and the bind variables with their types and values.</summary>
public sealed record SqlTabState(string Title, string Text, IReadOnlyList<Query.SqlVariable> Variables);

/// <summary>A LINQ console tab: its title, the code and the variables the code uses.</summary>
public sealed record LinqTabState(string Title, string Code, string Variables)
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
