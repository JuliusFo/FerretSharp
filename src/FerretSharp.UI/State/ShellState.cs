using FerretSharp.Core.Connections;

namespace FerretSharp.UI.State;

public enum ConnectionDialogMode { New, Edit, Duplicate }

public sealed record ConnectionDialogRequest(ConnectionDialogMode Mode, ConnectionProfile? Profile);

/// <summary>
/// Shell-level UI state shared via a cascading value: which dialog is open and what is pending confirmation.
/// Components request actions here instead of knowing about each other.
/// </summary>
public sealed class ShellState
{
    public event Action? Changed;

    public ConnectionDialogRequest? ConnectionDialog { get; private set; }

    public ConnectionProfile? PendingDelete { get; private set; }

    public bool SwitcherOpen { get; private set; }

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
