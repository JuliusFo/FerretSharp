using FerretSharp.Core.Connections;
using FerretSharp.Core.Schema;

namespace FerretSharp.UI.State;

public enum ConnectionDialogMode { New, Edit, Duplicate }

public sealed record ConnectionDialogRequest(ConnectionDialogMode Mode, ConnectionProfile? Profile);

public enum ShellPage { Connections, Explorer }

/// <summary>
/// Shell-level UI state shared via a cascading value: current page, open tabs, dialogs.
/// Components request actions here instead of knowing about each other.
/// </summary>
public sealed class ShellState
{
    private readonly List<TableTab> _tabs = [];

    public event Action? Changed;

    /// <summary>Handled by the shell, which owns the connection lifecycle.</summary>
    public event Action<ConnectionProfile>? ConnectRequested;

    /// <summary>Global shortcuts for the active tab (Ctrl+Enter, F5); handled by its tab view.</summary>
    public event Action<TableTab, TabCommand>? TabCommandRequested;

    public ShellPage Page { get; private set; } = ShellPage.Connections;

    public IReadOnlyList<TableTab> Tabs => _tabs;

    public TableTab? ActiveTab { get; private set; }

    public ConnectionDialogRequest? ConnectionDialog { get; private set; }

    public ConnectionProfile? PendingDelete { get; private set; }

    public bool SwitcherOpen { get; private set; }

    public void Connect(ConnectionProfile profile)
    {
        Set(() =>
        {
            SwitcherOpen = false;
            _tabs.Clear();
            ActiveTab = null;
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

    /// <summary>Activates the tab of <paramref name="table"/> or opens a new one.</summary>
    public TableTab OpenTable(TableSummary table, TabMode? mode = null)
    {
        var tab = _tabs.FirstOrDefault(t => t.Table.Ref == table.Ref);
        Set(() =>
        {
            if (tab is null)
            {
                tab = new TableTab(table);
                _tabs.Add(tab);
            }

            if (mode is { } m)
            {
                tab.Mode = m;
            }

            ActiveTab = tab;
            Page = ShellPage.Explorer;
        });
        return tab!;
    }

    public void ActivateTab(TableTab tab) => Set(() => ActiveTab = tab);

    public void CloseTab(TableTab tab) => Set(() =>
    {
        var index = _tabs.IndexOf(tab);
        _tabs.Remove(tab);
        if (ActiveTab == tab)
        {
            ActiveTab = _tabs.Count == 0 ? null : _tabs[Math.Min(index, _tabs.Count - 1)];
        }
    });

    public void CloseAllTabs() => Set(() =>
    {
        _tabs.Clear();
        ActiveTab = null;
    });

    /// <summary>Drops tabs whose table no longer exists (after a schema refresh).</summary>
    public void RemoveTabsWhere(Func<TableTab, bool> predicate) => Set(() =>
    {
        _tabs.RemoveAll(t => predicate(t));
        if (ActiveTab is not null && !_tabs.Contains(ActiveTab))
        {
            ActiveTab = _tabs.LastOrDefault();
        }
    });

    public void RequestTabCommand(TabCommand command)
    {
        if (ActiveTab is { } tab && Page == ShellPage.Explorer)
        {
            TabCommandRequested?.Invoke(tab, command);
        }
    }

    public void NotifyChanged() => Changed?.Invoke();

    public void NewConnection() => Set(() => ConnectionDialog = new(ConnectionDialogMode.New, null));

    public void EditConnection(ConnectionProfile profile) => Set(() => ConnectionDialog = new(ConnectionDialogMode.Edit, profile));

    public void DuplicateConnection(ConnectionProfile profile) => Set(() => ConnectionDialog = new(ConnectionDialogMode.Duplicate, profile));

    public void CloseConnectionDialog() => Set(() => ConnectionDialog = null);

    public void RequestDelete(ConnectionProfile profile) => Set(() => PendingDelete = profile);

    public void CancelDelete() => Set(() => PendingDelete = null);

    public void OpenSwitcher() => Set(() => SwitcherOpen = true);

    public void CloseSwitcher() => Set(() => SwitcherOpen = false);

    private void Set(Action change)
    {
        change();
        Changed?.Invoke();
    }
}

public enum TabCommand { ApplyFilters, Refresh }
