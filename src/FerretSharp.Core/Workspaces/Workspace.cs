using FerretSharp.Core.Query;
using FerretSharp.Core.Schema;

namespace FerretSharp.Core.Workspaces;

public enum TabMode
{
    Data,
    Structure,
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
    int? FirstVisibleRow = null);

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
}
