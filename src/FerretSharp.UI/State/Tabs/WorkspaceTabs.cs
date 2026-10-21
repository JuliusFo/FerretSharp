namespace FerretSharp.UI.State;

/// <summary>Tabs of one open workspace.</summary>
/// <param name="connectionId">The connection the workspace belongs to (WP-24: several can be open).</param>
public sealed class WorkspaceTabs(Guid workspaceId, Guid connectionId)
{
    public Guid WorkspaceId { get; } = workspaceId;

    public Guid ConnectionId { get; } = connectionId;

    public List<WorkspaceTab> Tabs { get; } = [];

    public WorkspaceTab? ActiveTab { get; set; }

    public int ActiveIndex => ActiveTab is null ? -1 : Tabs.IndexOf(ActiveTab);

    /// <summary>The pinned tabs stand first (WP-34); this many.</summary>
    public int PinnedCount => Tabs.TakeWhile(t => t.Pinned).Count();

    /// <summary>The table tabs (editing, FK navigation); LINQ tabs have no rows of their own.</summary>
    public IEnumerable<TableTab> TableTabs => Tabs.OfType<TableTab>();
}

/// <summary><see cref="Reload"/>: fetch the loaded rows again in place (after writing) – unlike <see cref="Refresh"/>, which starts at the top.</summary>
public enum TabCommand { ApplyFilters, Refresh, FindColumn, Reload, RunScript, FocusRow }

// The tabs of the open workspaces of all open connections: the tab part of the shell state (R3b, a file of its own).
