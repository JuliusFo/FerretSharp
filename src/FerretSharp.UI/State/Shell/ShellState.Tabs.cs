using FerretSharp.Core.Data;
using FerretSharp.Core.Links;
using FerretSharp.Core.Query;
using FerretSharp.Core.Relations;
using FerretSharp.Core.Resources;
using FerretSharp.UI.Resources;
using FerretSharp.Core.Schema;
using FerretSharp.Core.Workspaces;

namespace FerretSharp.UI.State;

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
        _schemas[connectionId] = schema;
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
        _schemas.Remove(connectionId);
        if (connectionId == CurrentConnection)
        {
            FollowCurrent();
        }
    });

    /// <summary>Another connection is shown (WP-24): its active workspace and tabs become the active ones.</summary>
    public void ShowConnection(Guid? connectionId) => Set(() =>
    {
        // The user went elsewhere: what waits for another connection does not open later on its own.
        DropPendingExcept(connectionId);

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

        OpenPendingSql();
        OpenPendingLink();
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
        tab.SetFilterRows(filters);
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
    /// variables ("Open in SQL editor" from a table tab's SQL preview).
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

    /// <summary>
    /// A record diagram of <paramref name="rows"/> of <paramref name="table"/> after the active tab (WP-60). Rows whose key has
    /// no exact text (<see cref="RecordKeys"/>) are left out; null if none is left.
    /// </summary>
    public RecordTab? OpenRecords(TableSummary table, TableDetails details, IReadOnlyList<RowData> rows)
    {
        if (ActiveWorkspace is not { } workspace)
        {
            return null;
        }

        var keys = rows.Select(r => RecordKeys.ToText(details, r.Key)).OfType<IReadOnlyList<string>>()
            .DistinctBy(k => string.Join("", k)).ToList();
        if (keys.Count == 0)
        {
            return null;
        }

        var rowIds = QueryBuilder.RowKeyOf(details) == RowKeyKind.RowId;
        var tab = new RecordTab(workspace.WorkspaceId, table, new RecordDiagramState(table.Ref, rowIds, keys, steps: null)) { Visited = true };
        return InsertAfterActive(workspace, tab);
    }

    /// <summary>A tab for a new table in the designer (WP-23, stage 3), after the active tab; the table goes into <paramref name="owner"/>.</summary>
    public NewTableTab? OpenNewTable(string owner)
    {
        if (ActiveWorkspace is not { } workspace)
        {
            return null;
        }

        var tab = InsertAfterActive(workspace, new NewTableTab(workspace.WorkspaceId, owner) { Visited = true });
        tab.Designer.AddColumn(); // something to start with: the key, usually
        return tab;
    }

    /// <summary>
    /// Activates the tab of a PL/SQL unit in the active workspace or opens a new one after the active tab (WP-28),
    /// optionally on a view or at a line of its source (a compile error, a search hit).
    /// </summary>
    public PlSqlTab? OpenPlSql(PlSqlObjectSummary unit, PlSqlView? view = null, SourcePosition? at = null)
    {
        if (ActiveWorkspace is not { } workspace)
        {
            return null;
        }

        var tab = workspace.Tabs.OfType<PlSqlTab>().FirstOrDefault(t => t.Unit.Ref == unit.Ref);
        if (tab is null)
        {
            tab = InsertAfterActive(workspace, new PlSqlTab(workspace.WorkspaceId, unit) { Visited = true });
        }

        Set(() =>
        {
            if (at is not null)
            {
                tab.ShowSource(at.Part, at.Line, at.Column);
            }
            else if (view is { } v && tab.Views.Contains(v))
            {
                tab.View = v;
            }

            tab.Visited = true;
            workspace.ActiveTab = tab;
            Page = ShellPage.Explorer;
        });
        return tab;
    }

    /// <summary>A new tab right after the active one (or at the end) – after the pinned tabs, if the active one is pinned –, activated, with the explorer shown.</summary>
    private T InsertAfterActive<T>(WorkspaceTabs workspace, T tab) where T : WorkspaceTab
    {
        Set(() =>
        {
            var index = workspace.ActiveTab is { } active ? workspace.Tabs.IndexOf(active) + 1 : workspace.Tabs.Count;
            index = Math.Max(index, workspace.PinnedCount); // never among the pinned tabs (WP-34)
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

    /// <summary>"Back" (Alt+←): activates the tab the active one was opened from by an FK jump.</summary>
    public void GoBack()
    {
        if (ActiveTab is TableTab tab && BackTarget() is { } target)
        {
            target.Forward = tab;
            ActivateTab(target);
        }
    }

    /// <summary>"Forward" (Alt+→): returns to the tab the user went back from.</summary>
    public void GoForward()
    {
        if (ForwardTarget() is { } target)
        {
            ActivateTab(target);
        }
    }

    /// <summary>Where <see cref="GoBack"/> would go; null if nowhere (the command palette greys it out, WP-38).</summary>
    public TableTab? BackTarget() =>
        ActiveTab is TableTab tab && Page == ShellPage.Explorer ? tab.BackTarget(Tabs.ToList()) : null;

    /// <summary>Where <see cref="GoForward"/> would go; null if nowhere.</summary>
    public TableTab? ForwardTarget() =>
        ActiveTab is TableTab tab && Page == ShellPage.Explorer ? tab.ForwardTarget(Tabs.ToList()) : null;

    /// <summary>
    /// Shows an open tab of the active workspace with the explorer (quick open, WP-38): that tab, also if another tab has
    /// the same table (FK jumps open a second one).
    /// </summary>
    public void ShowTab(WorkspaceTab tab) => Set(() =>
    {
        if (ActiveWorkspace is { } workspace && workspace.Tabs.Contains(tab))
        {
            tab.Visited = true;
            workspace.ActiveTab = tab;
            Page = ShellPage.Explorer;
        }
    });

    public void ActivateTab(WorkspaceTab tab) => Set(() =>
    {
        if (ActiveWorkspace is { } workspace && workspace.Tabs.Contains(tab))
        {
            tab.Visited = true;
            workspace.ActiveTab = tab;
        }
    });

    /// <summary>
    /// Pins or unpins a tab (WP-34). Pinned tabs stand first: a pinned tab goes to the end of them, an unpinned one right
    /// after them, so the order of the others stays.
    /// </summary>
    public void SetPinned(WorkspaceTab tab, bool pinned) => Set(() =>
    {
        if (FindWorkspace(tab.WorkspaceId) is not { } workspace || tab.Pinned == pinned || !workspace.Tabs.Remove(tab))
        {
            return;
        }

        tab.Pinned = pinned;
        workspace.Tabs.Insert(workspace.PinnedCount, tab);
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

    /// <summary>
    /// Closes several tabs of one workspace (right-click menu of a tab, 3.27). The active tab stays if it stays open, else
    /// <paramref name="target"/> (the tab the menu was used on) becomes active, else the next one to the right of the old
    /// active tab, or the last one.
    /// </summary>
    public void CloseTabs(WorkspaceTab target, IReadOnlyCollection<WorkspaceTab> tabs) => Set(() =>
    {
        if (FindWorkspace(target.WorkspaceId) is not { } workspace || tabs.Count == 0)
        {
            return;
        }

        var activeIndex = workspace.ActiveIndex;
        var after = workspace.Tabs.Skip(Math.Max(activeIndex, 0)).Where(t => !tabs.Contains(t)).ToList();
        workspace.Tabs.RemoveAll(tabs.Contains);
        if (workspace.ActiveTab is { } active && workspace.Tabs.Contains(active))
        {
            return;
        }

        workspace.ActiveTab = workspace.Tabs.Contains(target) ? target : after.FirstOrDefault() ?? workspace.Tabs.LastOrDefault();
        workspace.ActiveTab?.Visited = true;
    });

    public void CloseAllTabs() => Set(() =>
    {
        ActiveWorkspace?.Tabs.Clear();
        ActiveWorkspace?.ActiveTab = null;
    });

    /// <summary>
    /// After a schema refresh: drops the table and PL/SQL tabs of the connection whose object is gone – in the workspaces of
    /// that connection only, another connection's schema may well have the object.
    /// </summary>
    public void RemoveVanishedTabs(Guid connectionId, SchemaCache schema) => RemoveWhere(connectionId, tab => tab switch
    {
        TableTab table => Vanished(table, schema),
        PlSqlTab plSql => schema.FindPlSql(plSql.Unit.Ref) is null,
        RecordTab records => schema.Find(records.Table.Ref) is null && string.Equals(records.Table.Owner, schema.Owner, StringComparison.Ordinal),
        _ => false,
    });

    /// <summary>
    /// A saved tab on a table of another schema that no synonym reaches: it was opened by name and is restored the same way
    /// (review 3.40.0, A5) – before, it was dropped on every start. A table of the browsed schema that is gone stays dropped.
    /// </summary>
    private static TableSummary? OpenedByName(TableRef table, SchemaCache schema) =>
        string.Equals(table.Owner, schema.Owner, StringComparison.Ordinal) ? null : new TableSummary(table.Owner, table.Name, TableKind.Table);

    /// <summary>
    /// A table tab whose table is gone from the browsed schema (review 3.40.0, A5). A table of another schema that no synonym
    /// reaches was opened by name (an FK jump, <see cref="FkTargets.SummaryOf"/>) and is never in the cache – its tab stays, as
    /// does a tab with pending or written changes: closing it lost them without a word. Its next load names the problem.
    /// </summary>
    private static bool Vanished(TableTab tab, SchemaCache schema) =>
        schema.Find(tab.Table.Ref) is null
        && string.Equals(tab.Table.Owner, schema.Owner, StringComparison.Ordinal)
        && tab.Changes is not { PendingCount: > 0 } and not { FlushedCount: > 0 };

    private void RemoveWhere(Guid connectionId, Predicate<WorkspaceTab> predicate) => Set(() =>
    {
        foreach (var workspace in _workspaces.Where(w => w.ConnectionId == connectionId))
        {
            workspace.Tabs.RemoveAll(predicate);
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
                tab = LinqTab.Restore(workspace.Id, linq, state.Form);
            }
            else if (state.Sql is { } sql)
            {
                tab = SqlTab.Restore(workspace.Id, sql, state.Form);
            }
            else if (state.Records is { Owner: not null, Name: not null } records)
            {
                if ((schema.Find(records.Table) ?? OpenedByName(records.Table, schema)) is not { } table)
                {
                    continue; // dropped, or no longer reachable through a synonym
                }

                tab = RecordTab.Restore(workspace.Id, table, records);
            }
            else if (state.PlSql is { } plSql)
            {
                if (schema.FindPlSql(plSql.Ref) is not { } unit)
                {
                    continue; // dropped, or no longer reachable through a synonym
                }

                tab = PlSqlTab.Restore(workspace.Id, unit, plSql);
            }
            else if ((schema.Find(state.Table) ?? OpenedByName(state.Table, schema)) is { } table)
            {
                var tableTab = TableTab.Restore(workspace.Id, table, state);
                restored[i] = tableTab;
                tab = tableTab;
            }
            else
            {
                continue; // dropped, or no longer reachable through a synonym
            }

            tab.Pinned = state.Pinned;
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

        // Pinned tabs first (WP-34), whatever order the file has.
        var ordered = result.Tabs.Where(t => t.Pinned).Concat(result.Tabs.Where(t => !t.Pinned)).ToList();
        result.Tabs.Clear();
        result.Tabs.AddRange(ordered);
        result.ActiveTab = active ?? result.Tabs.FirstOrDefault();
        return result;
    }
}
