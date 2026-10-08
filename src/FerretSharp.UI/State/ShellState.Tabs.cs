using FerretSharp.Core.Query;
using FerretSharp.Core.Schema;
using FerretSharp.Core.Workspaces;

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

    /// <summary>The table tabs (editing, FK navigation); LINQ tabs have no rows of their own.</summary>
    public IEnumerable<TableTab> TableTabs => Tabs.OfType<TableTab>();
}

/// <summary><see cref="Reload"/>: fetch the loaded rows again in place (after writing) – unlike <see cref="Refresh"/>, which starts at the top.</summary>
public enum TabCommand { ApplyFilters, Refresh, FindColumn, Reload, RunScript }

// The tabs of the open workspaces of all open connections: the tab part of the shell state (R3b, a file of its own).
public sealed partial class ShellState
{
    private readonly List<WorkspaceTabs> _workspaces = [];

    /// <summary>Per open connection its active workspace, kept while the connection is in the background.</summary>
    private readonly Dictionary<Guid, Guid> _activeByConnection = [];

    /// <summary>Open workspaces of all open connections (their tabs stay mounted); per connection in bar order.</summary>
    public IReadOnlyList<WorkspaceTabs> AllWorkspaces => _workspaces;

    /// <summary>Open workspaces of the shown connection, in bar order.</summary>
    public IReadOnlyList<WorkspaceTabs> Workspaces => _workspaces.Where(w => w.ConnectionId == CurrentConnection).ToList();

    /// <summary>A workspace's tabs, whichever connection it belongs to.</summary>
    public WorkspaceTabs? FindWorkspace(Guid workspaceId) => _workspaces.FirstOrDefault(w => w.WorkspaceId == workspaceId);

    /// <summary>The connection shown (WP-24); null if none.</summary>
    public Guid? CurrentConnection { get; private set; }

    public WorkspaceTabs? ActiveWorkspace { get; private set; }

    public IReadOnlyList<WorkspaceTab> Tabs => ActiveWorkspace?.Tabs ?? [];

    public WorkspaceTab? ActiveTab => ActiveWorkspace?.ActiveTab;

    /// <summary>
    /// Aligns the tabs of one connection with its open workspaces: restores tabs of newly opened ones from their saved
    /// state (tables that no longer exist are dropped), forgets closed ones and follows its active workspace. Other
    /// connections' workspaces stay as they are.
    /// </summary>
    public void SyncWorkspaces(Guid connectionId, IReadOnlyList<Workspace> open, Guid? activeId, SchemaCache schema) => Set(() =>
    {
        var existing = _workspaces.Where(w => w.ConnectionId == connectionId).ToDictionary(w => w.WorkspaceId);
        var at = _workspaces.FindIndex(w => w.ConnectionId == connectionId);
        _workspaces.RemoveAll(w => w.ConnectionId == connectionId);
        var synced = open.Select(workspace => existing.GetValueOrDefault(workspace.Id) ?? Restore(workspace, connectionId, schema)).ToList();
        _workspaces.InsertRange(at < 0 ? _workspaces.Count : at, synced);

        var active = synced.FirstOrDefault(w => w.WorkspaceId == activeId) ?? synced.FirstOrDefault();
        if (active is null)
        {
            _activeByConnection.Remove(connectionId);
        }
        else
        {
            _activeByConnection[connectionId] = active.WorkspaceId;
        }

        if (connectionId == CurrentConnection)
        {
            FollowCurrent();
        }
    });

    /// <summary>Forgets the workspace tabs of a connection (disconnected). Their state is saved by then.</summary>
    public void ClearWorkspaces(Guid connectionId) => Set(() =>
    {
        _workspaces.RemoveAll(w => w.ConnectionId == connectionId);
        _activeByConnection.Remove(connectionId);
        if (connectionId == CurrentConnection)
        {
            FollowCurrent();
        }
    });

    /// <summary>Another connection is shown (WP-24): its active workspace and tabs become the active ones.</summary>
    public void ShowConnection(Guid? connectionId) => Set(() =>
    {
        CurrentConnection = connectionId;
        SwitcherOpen = false;
        FollowCurrent();
    });

    private void FollowCurrent()
    {
        ActiveWorkspace = CurrentConnection is { } id && _activeByConnection.TryGetValue(id, out var active)
            ? _workspaces.FirstOrDefault(w => w.WorkspaceId == active)
            : null;
        if (ActiveWorkspace?.ActiveTab is { } tab)
        {
            tab.Visited = true;
        }
    }

    /// <summary>Activates the tab of <paramref name="table"/> in the active workspace or opens a new one.</summary>
    public TableTab? OpenTable(TableSummary table, TabMode? mode = null)
    {
        if (ActiveWorkspace is not { } workspace)
        {
            return null;
        }

        var tab = workspace.Tabs.OfType<TableTab>().FirstOrDefault(t => t.Table.Ref == table.Ref);
        Set(() =>
        {
            if (tab is null)
            {
                tab = new TableTab(workspace.WorkspaceId, table);
                workspace.Tabs.Add(tab);
            }

            if (mode is { } m)
            {
                tab.Mode = m;
            }

            tab.Visited = true;
            workspace.ActiveTab = tab;
            Page = ShellPage.Explorer;
        });
        return tab;
    }

    /// <summary>
    /// Opens a new tab (also if the table is already open) with the filters applied – used for FK jumps, so the tab
    /// the user came from keeps its own filters. It is placed right after the active tab.
    /// </summary>
    public TableTab? OpenFiltered(TableSummary table, IReadOnlyList<FilterCondition> filters)
    {
        if (ActiveWorkspace is not { } workspace)
        {
            return null;
        }

        var tab = new TableTab(workspace.WorkspaceId, table) { AppliedFilters = filters, Visited = true, Origin = workspace.ActiveTab as TableTab };
        tab.FilterRows.AddRange(filters.Select(FilterRow.From));
        if (workspace.ActiveTab is TableTab origin)
        {
            origin.Forward = null; // a new jump replaces the way forward, like in a browser
        }

        return InsertAfterActive(workspace, tab);
    }

    /// <summary>Opens a new LINQ console tab ("LINQ 1", "LINQ 2" …) after the active tab (WP-13).</summary>
    public LinqTab? OpenLinq(string? code = null)
    {
        if (ActiveWorkspace is not { } workspace)
        {
            return null;
        }

        var title = Workspace.FirstFreeName("LINQ ", workspace.Tabs.OfType<LinqTab>().Select(t => t.Title));
        return InsertAfterActive(workspace, new LinqTab(workspace.WorkspaceId, title) { Code = code ?? "", Visited = true });
    }

    /// <summary>
    /// Opens a new SQL editor tab ("SQL 1", "SQL 2" …) after the active tab (WP-17), optionally with a statement and its
    /// variables ("In SQL-Editor öffnen" from a table tab's SQL preview).
    /// </summary>
    public SqlTab? OpenSql(string? text = null, IReadOnlyList<Core.Query.SqlVariable>? variables = null)
    {
        if (ActiveWorkspace is not { } workspace)
        {
            return null;
        }

        var title = Workspace.FirstFreeName("SQL ", workspace.Tabs.OfType<SqlTab>().Select(t => t.Title));
        return InsertAfterActive(workspace, new SqlTab(workspace.WorkspaceId, title) { Text = text ?? "", Variables = variables ?? [], Visited = true });
    }

    /// <summary>A new tab right after the active one (or at the end), activated, with the explorer shown.</summary>
    private T InsertAfterActive<T>(WorkspaceTabs workspace, T tab) where T : WorkspaceTab
    {
        Set(() =>
        {
            var index = workspace.ActiveTab is { } active ? workspace.Tabs.IndexOf(active) + 1 : workspace.Tabs.Count;
            workspace.Tabs.Insert(index, tab);
            workspace.ActiveTab = tab;
            Page = ShellPage.Explorer;
        });
        return tab;
    }

    /// <summary>Renames a SQL or LINQ tab (saved with the workspace); an empty or too long name changes nothing.</summary>
    public void RenameTab(ITitledTab tab, string? name)
    {
        if (Workspace.NormalizeName(name) is { } title && title != tab.Title)
        {
            Set(() => tab.Title = title);
        }
    }

    /// <summary>"Zurück" (Alt+←): activates the tab the active one was opened from by an FK jump.</summary>
    public void GoBack()
    {
        if (ActiveTab is TableTab tab && Page == ShellPage.Explorer && tab.BackTarget(Tabs.ToList()) is { } target)
        {
            target.Forward = tab;
            ActivateTab(target);
        }
    }

    /// <summary>"Vor" (Alt+→): returns to the tab the user went back from.</summary>
    public void GoForward()
    {
        if (ActiveTab is TableTab tab && Page == ShellPage.Explorer && tab.ForwardTarget(Tabs.ToList()) is { } target)
        {
            ActivateTab(target);
        }
    }

    public void ActivateTab(WorkspaceTab tab) => Set(() =>
    {
        if (ActiveWorkspace is { } workspace && workspace.Tabs.Contains(tab))
        {
            tab.Visited = true;
            workspace.ActiveTab = tab;
        }
    });

    /// <summary>Closes a tab of any open workspace (an error in a background connection's tab offers to close it too).</summary>
    public void CloseTab(WorkspaceTab tab) => Set(() =>
    {
        if (FindWorkspace(tab.WorkspaceId) is not { } workspace)
        {
            return;
        }

        var index = workspace.Tabs.IndexOf(tab);
        workspace.Tabs.Remove(tab);
        if (workspace.ActiveTab == tab)
        {
            workspace.ActiveTab = workspace.Tabs.Count == 0 ? null : workspace.Tabs[Math.Min(index, workspace.Tabs.Count - 1)];
            workspace.ActiveTab?.Visited = true;
        }
    });

    public void CloseAllTabs() => Set(() =>
    {
        ActiveWorkspace?.Tabs.Clear();
        ActiveWorkspace?.ActiveTab = null;
    });

    /// <summary>
    /// Drops tabs whose table no longer exists (after a schema refresh) in the workspaces of that connection only – another
    /// connection's schema may well have the table.
    /// </summary>
    public void RemoveTabsWhere(Guid connectionId, Func<TableTab, bool> predicate) => Set(() =>
    {
        foreach (var workspace in _workspaces.Where(w => w.ConnectionId == connectionId))
        {
            workspace.Tabs.RemoveAll(t => t is TableTab table && predicate(table));
            if (workspace.ActiveTab is not null && !workspace.Tabs.Contains(workspace.ActiveTab))
            {
                workspace.ActiveTab = workspace.Tabs.LastOrDefault();
                workspace.ActiveTab?.Visited = true;
            }
        }
    });

    public void RequestTabCommand(TabCommand command)
    {
        if (ActiveTab is { } tab && Page == ShellPage.Explorer)
        {
            TabCommandRequested?.Invoke(tab, command);
        }
    }

    /// <summary>A command for a particular tab, also an inactive one (reload after writing, commit, rollback).</summary>
    public void RequestTabCommand(WorkspaceTab tab, TabCommand command) => TabCommandRequested?.Invoke(tab, command);

    /// <summary>
    /// After a write in the workspace's transaction (a statement, undo, a lost session): its table tabs fetch the loaded rows
    /// again, they may show changed ones. Whichever connection it belongs to.
    /// </summary>
    public void ReloadTableTabs(Guid workspaceId)
    {
        foreach (var tab in FindWorkspace(workspaceId)?.TableTabs ?? [])
        {
            RequestTabCommand(tab, TabCommand.Reload);
        }
    }

    private static WorkspaceTabs Restore(Workspace workspace, Guid connectionId, SchemaCache schema)
    {
        var result = new WorkspaceTabs(workspace.Id, connectionId);
        var restored = new Dictionary<int, TableTab>(); // saved index → tab
        WorkspaceTab? active = null;
        for (var i = 0; i < workspace.Tabs.Count; i++)
        {
            var state = workspace.Tabs[i];
            WorkspaceTab tab;
            if (state.Linq is { } linq)
            {
                tab = LinqTab.Restore(workspace.Id, linq);
            }
            else if (state.Sql is { } sql)
            {
                tab = SqlTab.Restore(workspace.Id, sql);
            }
            else if (schema.Find(state.Table) is { } table)
            {
                var tableTab = TableTab.Restore(workspace.Id, table, state);
                restored[i] = tableTab;
                tab = tableTab;
            }
            else
            {
                continue; // dropped, or no longer reachable through a synonym
            }

            result.Tabs.Add(tab);
            if (i == workspace.ActiveTabIndex)
            {
                active = tab;
            }
        }

        foreach (var (i, tab) in restored)
        {
            if (workspace.Tabs[i].OriginTab is { } origin && origin != i)
            {
                tab.Origin = restored.GetValueOrDefault(origin);
            }
        }

        result.ActiveTab = active ?? result.Tabs.FirstOrDefault();
        return result;
    }
}
