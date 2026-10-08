using FerretSharp.Core.Connections;
using FerretSharp.Core.Query;
using FerretSharp.Core.Schema;
using FerretSharp.Core.Workspaces;

namespace FerretSharp.UI.State;

public enum ConnectionDialogMode { New, Edit, Duplicate }

public sealed record ConnectionDialogRequest(ConnectionDialogMode Mode, ConnectionProfile? Profile);

public enum ShellPage { Connections, Explorer, Settings, Model, Compare }

/// <summary>
/// Shell-level UI state shared via a cascading value: current page, dialogs, notices, the events between components –
/// and the tabs per open workspace (<c>ShellState.Tabs.cs</c>). Components request actions here instead of knowing about
/// each other.
/// </summary>
public sealed partial class ShellState
{
    public event Action? Changed;

    /// <summary>
    /// Tab state changed without anything to re-render (filter typing, scrolling, sorting); the shell saves it.
    /// <see cref="Changed"/> implies this too.
    /// </summary>
    public event Action? Dirty;

    /// <summary>Handled by the shell, which owns the connection lifecycle.</summary>
    public event Action<ConnectionProfile>? ConnectRequested;

    /// <summary>Global shortcuts for the active tab (Ctrl+Enter, F5); handled by its tab view.</summary>
    public event Action<WorkspaceTab, TabCommand>? TabCommandRequested;

    /// <summary>
    /// Right before a workspace writes or commits (Ctrl+S, commit): the forms take over values typed but not yet
    /// confirmed (WP-21), so they are not silently left out. A handler returns why writing must not start (a typed
    /// value is invalid); null if it may.
    /// </summary>
    public event Func<Guid, string?>? BeforeWrite;

    /// <summary>Runs every <see cref="BeforeWrite"/> handler for the workspace; the first reason against writing, or null.</summary>
    public string? PrepareWrite(Guid workspaceId) =>
        (BeforeWrite?.GetInvocationList() ?? [])
            .Cast<Func<Guid, string?>>()
            .Select(handler => handler(workspaceId))
            .ToList()
            .FirstOrDefault(reason => reason is not null);

    public ShellPage Page { get; private set; } = ShellPage.Connections;

    public ConnectionDialogRequest? ConnectionDialog { get; private set; }

    public ConnectionProfile? PendingDelete { get; private set; }

    public bool SwitcherOpen { get; private set; }

    /// <summary>Shown in the error dialog (code, message, statement).</summary>
    public DatabaseException? ErrorDetails { get; private set; }

    /// <summary>Short-lived message (export done, warnings), shown as a toast.</summary>
    public Notice? Notice { get; private set; }

    /// <summary>Shows the connection, opening it if needed (WP-24). Whether it was lost is the lifecycle's business: switching away keeps it.</summary>
    public void Connect(ConnectionProfile profile)
    {
        Set(() =>
        {
            SwitcherOpen = false;
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

    /// <summary>Schema comparison of several connections (WP-20); needs no active connection.</summary>
    public void ShowCompare() => Set(() =>
    {
        SwitcherOpen = false;
        Page = ShellPage.Compare;
    });

    /// <summary>The C# model of the connection's linked project and its differences to the schema (WP-11).</summary>
    public void ShowModel() => Set(() =>
    {
        SwitcherOpen = false;
        Page = ShellPage.Model;
    });

    /// <summary>Workspace the user wants to unlock for writing; the shell asks first (WP-10).</summary>
    public Guid? PendingUnlock { get; private set; }

    public void RequestUnlock(Guid workspaceId) => Set(() => PendingUnlock = workspaceId);

    public void CloseUnlock() => Set(() => PendingUnlock = null);

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

    private void Set(Action change)
    {
        change();
        NotifyChanged();
    }
}

/// <summary>Leaving workspaces with uncommitted changes (close, disconnect, switch connection, exit).</summary>
/// <param name="What">What is about to happen, e.g. "Workspace schließen".</param>
/// <param name="WorkspaceIds">Workspaces whose changes are at stake.</param>
/// <param name="Continue">Runs after the changes were committed or discarded.</param>
public sealed record LeaveRequest(string What, IReadOnlyList<Guid> WorkspaceIds, Func<Task> Continue);

/// <param name="Warnings">Shown below the text, e.g. LOB values that were not exported.</param>
public sealed record Notice(string Text, IReadOnlyList<string> Warnings, DateTimeOffset At);
