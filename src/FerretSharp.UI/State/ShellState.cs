using FerretSharp.Core.Connections;
using FerretSharp.Core.Schema;

namespace FerretSharp.UI.State;

public enum ConnectionDialogMode { New, Edit, Duplicate }

public sealed record ConnectionDialogRequest(ConnectionDialogMode Mode, ConnectionProfile? Profile);

public enum ShellPage { Connections, Explorer }

/// <summary>
/// Shell-level UI state shared via a cascading value: current page, open dialogs, selection.
/// Components request actions here instead of knowing about each other.
/// </summary>
public sealed class ShellState
{
    public event Action? Changed;

    /// <summary>Handled by the shell, which owns the connection lifecycle.</summary>
    public event Action<ConnectionProfile>? ConnectRequested;

    public ShellPage Page { get; private set; } = ShellPage.Connections;

    public TableSummary? SelectedTable { get; private set; }

    public ConnectionDialogRequest? ConnectionDialog { get; private set; }

    public ConnectionProfile? PendingDelete { get; private set; }

    public bool SwitcherOpen { get; private set; }

    public void Connect(ConnectionProfile profile)
    {
        Set(() =>
        {
            SwitcherOpen = false;
            SelectedTable = null;
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

    public void SelectTable(TableSummary? table) => Set(() => SelectedTable = table);

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
