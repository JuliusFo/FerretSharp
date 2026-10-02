using FerretSharp.Core.Connections;
using FerretSharp.Core.Query;
using FerretSharp.Core.Schema;
using FerretSharp.Core.Workspaces;

namespace FerretSharp.UI.State;

public enum ConnectionDialogMode { New, Edit, Duplicate }

public sealed record ConnectionDialogRequest(ConnectionDialogMode Mode, ConnectionProfile? Profile);

public enum ShellPage { Connections, Explorer, Settings }

/// <summary>Tabs of one open workspace.</summary>
public sealed class WorkspaceTabs(Guid workspaceId)
{
    public Guid WorkspaceId { get; } = workspaceId;

    public List<TableTab> Tabs { get; } = [];

    public TableTab? ActiveTab { get; set; }

    public int ActiveIndex => ActiveTab is null ? -1 : Tabs.IndexOf(ActiveTab);
}

/// <summary>
/// Shell-level UI state shared via a cascading value: current page, tabs per open workspace, dialogs.
/// Components request actions here instead of knowing about each other.
/// </summary>
public sealed class ShellState
{
    private readonly List<WorkspaceTabs> _workspaces = [];

    public event Action? Changed;

    /// <summary>
    /// Tab state changed without anything to re-render (filter typing, scrolling, sorting); the shell saves it.
    /// <see cref="Changed"/> implies this too.
    /// </summary>
    public event Action? Dirty;

    /// <summary>Handled by the shell, which owns the connection lifecycle.</summary>
    public event Action<ConnectionProfile>? ConnectRequested;

    /// <summary>Global shortcuts for the active tab (Ctrl+Enter, F5); handled by its tab view.</summary>
    public event Action<TableTab, TabCommand>? TabCommandRequested;

    public ShellPage Page { get; private set; } = ShellPage.Connections;

    /// <summary>Open workspaces in bar order.</summary>
    public IReadOnlyList<WorkspaceTabs> Workspaces => _workspaces;

    public WorkspaceTabs? ActiveWorkspace { get; private set; }

    public IReadOnlyList<TableTab> Tabs => ActiveWorkspace?.Tabs ?? [];

    public TableTab? ActiveTab => ActiveWorkspace?.ActiveTab;

    public ConnectionDialogRequest? ConnectionDialog { get; private set; }

    public ConnectionProfile? PendingDelete { get; private set; }

    public bool SwitcherOpen { get; private set; }

    /// <summary>Shown in the error dialog (code, message, statement).</summary>
    public DatabaseException? ErrorDetails { get; private set; }

    /// <summary>Set when a query found the session gone; the shell offers to reconnect.</summary>
    public DatabaseException? ConnectionLost { get; private set; }

    /// <summary>Short-lived message (export done, warnings), shown as a toast.</summary>
    public Notice? Notice { get; private set; }

    public void Connect(ConnectionProfile profile)
    {
        Set(() =>
        {
            SwitcherOpen = false;
            ConnectionLost = null;
            Page = ShellPage.Explorer;
        });
        ConnectRequested?.Invoke(profile);
    }

    public void ShowConnections() => Set(() =>
    {
        SwitcherOpen = false;
        Page = ShellPage.Connections;
    });

    public void ShowExplorer() => Set(() => Page = ShellPage.Explorer);

    public void ShowSettings() => Set(() =>
    {
        SwitcherOpen = false;
        Page = ShellPage.Settings;
    });

    /// <summary>
    /// Aligns the tabs with the open workspaces: restores tabs of newly opened ones from their saved state (tables
    /// that no longer exist are dropped), forgets closed ones and follows the active workspace.
    /// </summary>
    public void SyncWorkspaces(IReadOnlyList<Workspace> open, Guid? activeId, SchemaCache schema) => Set(() =>
    {
        var existing = _workspaces.ToDictionary(w => w.WorkspaceId);
        _workspaces.Clear();
        foreach (var workspace in open)
        {
            _workspaces.Add(existing.GetValueOrDefault(workspace.Id) ?? Restore(workspace, schema));
        }

        ActiveWorkspace = _workspaces.FirstOrDefault(w => w.WorkspaceId == activeId) ?? _workspaces.FirstOrDefault();
        if (ActiveWorkspace?.ActiveTab is { } tab)
        {
            tab.Visited = true;
        }
    });

    /// <summary>Forgets all workspace tabs (disconnect, switching connections). Their state is saved by then.</summary>
    public void ClearWorkspaces() => Set(() =>
    {
        _workspaces.Clear();
        ActiveWorkspace = null;
    });

    /// <summary>Activates the tab of <paramref name="table"/> in the active workspace or opens a new one.</summary>
    public TableTab? OpenTable(TableSummary table, TabMode? mode = null)
    {
        if (ActiveWorkspace is not { } workspace)
        {
            return null;
        }

        var tab = workspace.Tabs.FirstOrDefault(t => t.Table.Ref == table.Ref);
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

        var tab = new TableTab(workspace.WorkspaceId, table) { AppliedFilters = filters, Visited = true };
        tab.FilterRows.AddRange(filters.Select(FilterRow.From));
        Set(() =>
        {
            var index = workspace.ActiveTab is { } active ? workspace.Tabs.IndexOf(active) + 1 : workspace.Tabs.Count;
            workspace.Tabs.Insert(index, tab);
            workspace.ActiveTab = tab;
            Page = ShellPage.Explorer;
        });
        return tab;
    }

    public void ActivateTab(TableTab tab) => Set(() =>
    {
        if (ActiveWorkspace is { } workspace && workspace.Tabs.Contains(tab))
        {
            tab.Visited = true;
            workspace.ActiveTab = tab;
        }
    });

    public void CloseTab(TableTab tab) => Set(() =>
    {
        if (ActiveWorkspace is not { } workspace)
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

    /// <summary>Drops tabs whose table no longer exists (after a schema refresh), in all workspaces.</summary>
    public void RemoveTabsWhere(Func<TableTab, bool> predicate) => Set(() =>
    {
        foreach (var workspace in _workspaces)
        {
            workspace.Tabs.RemoveAll(t => predicate(t));
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

    public void NotifyChanged()
    {
        Changed?.Invoke();
        Dirty?.Invoke();
    }

    /// <summary>Tab state changed (see <see cref="Dirty"/>); no re-render.</summary>
    public void MarkDirty() => Dirty?.Invoke();

    public void NewConnection() => Set(() => ConnectionDialog = new(ConnectionDialogMode.New, null));

    public void EditConnection(ConnectionProfile profile) => Set(() => ConnectionDialog = new(ConnectionDialogMode.Edit, profile));

    public void DuplicateConnection(ConnectionProfile profile) => Set(() => ConnectionDialog = new(ConnectionDialogMode.Duplicate, profile));

    public void CloseConnectionDialog() => Set(() => ConnectionDialog = null);

    public void RequestDelete(ConnectionProfile profile) => Set(() => PendingDelete = profile);

    public void CancelDelete() => Set(() => PendingDelete = null);

    public void ShowError(DatabaseException error) => Set(() => ErrorDetails = error);

    public void CloseError() => Set(() => ErrorDetails = null);

    /// <summary>Every database failure of the UI goes through here, so a lost connection is reported once and loudly.</summary>
    public void ReportFailure(DatabaseException error)
    {
        if (error.IsConnectionLost && ConnectionLost is null)
        {
            Set(() => ConnectionLost = error);
        }
    }

    public void ClearConnectionLost() => Set(() => ConnectionLost = null);

    public void Notify(string text, IReadOnlyList<string>? warnings = null) =>
        Set(() => Notice = new Notice(text, warnings ?? [], DateTimeOffset.UtcNow));

    public void DismissNotice(Notice notice) => Set(() =>
    {
        if (Notice == notice)
        {
            Notice = null;
        }
    });

    public void OpenSwitcher() => Set(() => SwitcherOpen = true);

    public void CloseSwitcher() => Set(() => SwitcherOpen = false);

    private static WorkspaceTabs Restore(Workspace workspace, SchemaCache schema)
    {
        var result = new WorkspaceTabs(workspace.Id);
        TableTab? active = null;
        for (var i = 0; i < workspace.Tabs.Count; i++)
        {
            var state = workspace.Tabs[i];
            if (schema.Find(state.Table) is not { } table)
            {
                continue; // dropped, or no longer reachable through a synonym
            }

            var tab = TableTab.Restore(workspace.Id, table, state);
            result.Tabs.Add(tab);
            if (i == workspace.ActiveTabIndex)
            {
                active = tab;
            }
        }

        result.ActiveTab = active ?? result.Tabs.FirstOrDefault();
        return result;
    }

    private void Set(Action change)
    {
        change();
        NotifyChanged();
    }
}

public enum TabCommand { ApplyFilters, Refresh, FindColumn }

/// <param name="Warnings">Shown below the text, e.g. LOB values that were not exported.</param>
public sealed record Notice(string Text, IReadOnlyList<string> Warnings, DateTimeOffset At);
