using FerretSharp.Core.Links;
using FerretSharp.Core.Query;
using FerretSharp.Core.Resources;
using FerretSharp.UI.Resources;
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

    /// <summary>The pinned tabs stand first (WP-34); this many.</summary>
    public int PinnedCount => Tabs.TakeWhile(t => t.Pinned).Count();

    /// <summary>The table tabs (editing, FK navigation); LINQ tabs have no rows of their own.</summary>
    public IEnumerable<TableTab> TableTabs => Tabs.OfType<TableTab>();
}

/// <summary><see cref="Reload"/>: fetch the loaded rows again in place (after writing) – unlike <see cref="Refresh"/>, which starts at the top.</summary>
public enum TabCommand { ApplyFilters, Refresh, FindColumn, Reload, RunScript, FocusRow }

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

    /// <summary>The schema of each connection with workspaces (from <see cref="SyncWorkspaces"/>), to find a linked table.</summary>
    private readonly Dictionary<Guid, SchemaCache> _schemas = [];

    /// <summary>A link waiting for its connection to be shown with its workspaces (<see cref="OpenLinkIn"/>).</summary>
    private readonly PendingOpen<TableLink> _pendingLink = new();

    /// <summary>A script waiting for its connection to be shown with its workspaces (<see cref="OpenSqlIn"/>).</summary>
    private readonly PendingOpen<string> _pendingSql = new();

    private readonly ConnectionFailures _failures = new();

    /// <summary>
    /// Opens the table of a confirmed link (WP-37) in a new tab of the connection's active workspace – right away if the
    /// connection is shown, otherwise once it is (the caller shows or connects it). Only the latest link waits.
    /// </summary>
    public void OpenLinkIn(Guid connectionId, TableLink link)
    {
        _pendingLink.Wait(connectionId, link);
        Set(OpenPendingLink);
    }

    /// <summary>
    /// A connection's failure after each change of it: the error of the last attempt to connect, or why it was lost; null
    /// if none. A new failure drops a link or script waiting for that connection, so it does not open on its own when the
    /// user connects again later (<see cref="ConnectionFailures"/>).
    /// </summary>
    public void ReportConnectionFailure(Guid connectionId, object? failure) => _failures.Report(connectionId, failure, DropPendingFor);

    /// <summary>A link or script waiting for <paramref name="connectionId"/> no longer opens on its own: connecting failed or was cancelled.</summary>
    private void DropPendingFor(Guid connectionId)
    {
        _pendingLink.DropFor(connectionId);
        _pendingSql.DropFor(connectionId);
    }

    /// <summary>What waits for a connection other than <paramref name="shown"/> no longer opens on its own.</summary>
    private void DropPendingExcept(Guid? shown)
    {
        _pendingLink.DropExcept(shown);
        _pendingSql.DropExcept(shown);
    }

    private void OpenPendingLink()
    {
        if (ActiveWorkspace is not { } workspace || CurrentConnection is not { } shown || !_schemas.TryGetValue(shown, out var schema)
            || _pendingLink.Take(shown) is not { } link)
        {
            return;
        }

        if (LinkedTable(schema, link) is not { } table)
        {
            Notice = new Notice(TextFormat.Format(ShellText.Link_TableMissing, link.Table, schema.Owner), [], DateTimeOffset.UtcNow);
            return;
        }

        var tab = new TableTab(workspace.WorkspaceId, table) { AppliedFilters = link.Filters, Sorts = link.Sorts, Mode = link.Mode ?? TabMode.Data, Visited = true, FitToStructure = true };
        tab.SetFilterRows(link.Filters);
        InsertAfterActive(workspace, tab);
    }

    /// <summary>
    /// The linked table on this connection: by owner and name, else by name in the connection's schema (Test and Prod
    /// have the same tables, the schema name may differ), else a synonym of that name.
    /// </summary>
    public static TableSummary? LinkedTable(SchemaCache schema, TableLink link) =>
        schema.Find(new TableRef(link.Owner, link.Table))
        ?? schema.Tables.FirstOrDefault(t => t.Owner == schema.Owner && t.Name == link.Table && t.Synonym is null)
        ?? schema.Tables.FirstOrDefault(t => t.Synonym?.Name == link.Table); // not any other schema's table of that name

    /// <summary>
    /// Opens a SQL tab with <paramref name="text"/> in the active workspace of the connection (WP-22: the DDL proposal of
    /// the schema comparison, to run there) – right away if it is shown, otherwise once it is (the caller shows or
    /// connects it). Only the latest request waits.
    /// </summary>
    public void OpenSqlIn(Guid connectionId, string text)
    {
        _pendingSql.Wait(connectionId, text);
        OpenPendingSql();
    }

    private void OpenPendingSql()
    {
        if (ActiveWorkspace is not null && _pendingSql.Take(CurrentConnection) is { } text)
        {
            OpenSql(text);
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
