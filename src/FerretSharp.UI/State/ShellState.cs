using FerretSharp.Core.Connections;
using FerretSharp.Core.Data;
using FerretSharp.Core.Query;
using FerretSharp.Core.Schema;
using FerretSharp.Core.Workspaces;

namespace FerretSharp.UI.State;

public enum ConnectionDialogMode { New, Edit, Duplicate }

public sealed record ConnectionDialogRequest(ConnectionDialogMode Mode, ConnectionProfile? Profile);

/// <summary>The password to ask for before connecting (WP-35).</summary>
/// <param name="ShowWhenConnected">
/// Asked on the way to the explorer (<c>ShellState.Connect</c>): the connection is shown afterwards. Otherwise the page
/// that connected on its own (app statements, schema comparison) stays.
/// </param>
public sealed record PasswordRequest(ConnectionProfile Profile, bool ShowWhenConnected);

/// <summary>Export all connections, or – <paramref name="OnlyGroup"/> – those of one group (null = without group).</summary>
public sealed record ConnectionExportRequest(bool OnlyGroup = false, string? Group = null);

public enum ShellPage { Connections, Explorer, Settings, Model, Compare, AppStatements }

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

    /// <summary>
    /// Whether a tab has values typed but not yet confirmed (the form beside the grid, WP-21): closing the tab would lose
    /// them. A handler answers for its own tab only.
    /// </summary>
    public event Func<WorkspaceTab, bool>? TypedValuesOf;

    /// <summary>Whether any <see cref="TypedValuesOf"/> handler has typed values for the tab.</summary>
    public bool HasTypedValues(WorkspaceTab tab) =>
        (TypedValuesOf?.GetInvocationList() ?? []).Cast<Func<WorkspaceTab, bool>>().Any(handler => handler(tab));

    public ShellPage Page { get; private set; } = ShellPage.Connections;

    /// <summary>
    /// The page <see cref="Connect"/> left for the explorer, until the lifecycle takes it (<see cref="TakeConnectOrigin"/>):
    /// back there if the connection still needs a password.
    /// </summary>
    private (Guid ProfileId, ShellPage Page)? _connectOrigin;

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
            _connectOrigin = (profile.Id, Page);
            PendingPassword = null;
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

    /// <summary>The statements of a running app (WP-33); needs no active connection.</summary>
    public void ShowAppStatements() => Set(() =>
    {
        SwitcherOpen = false;
        Page = ShellPage.AppStatements;
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
        // Edits of a tab changed (discard, undo, revert …): a stale tab without changes takes the new structure (A30).
        foreach (var tab in _workspaces.SelectMany(w => w.TableTabs))
        {
            tab.DropStaleStructure();
        }

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

    /// <summary>An open connection was saved with changes that only a new connection applies (review 3.31.0, A8).</summary>
    public ConnectionProfile? ReconnectOffer { get; private set; }

    public void OfferReconnect(ConnectionProfile profile) => Set(() => ReconnectOffer = profile);

    public void CancelReconnectOffer() => Set(() => ReconnectOffer = null);

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

    /// <summary>The change overview of the active workspace is open beside the tabs (WP-30).</summary>
    public bool ChangesOpen { get; private set; }

    /// <summary>The user opened or closed the change overview; the shell remembers it in the settings.</summary>
    public event Action<bool>? ChangesOpenChanged;

    public void ToggleChanges() => SetChangesOpen(!ChangesOpen);

    public void CloseChanges() => SetChangesOpen(false);

    /// <summary>As it was when the app was closed (start, no event).</summary>
    public void RestoreChanges(bool open) => ChangesOpen = open;

    private void SetChangesOpen(bool open)
    {
        Set(() => ChangesOpen = open);
        ChangesOpenChanged?.Invoke(open);
    }

    /// <summary>
    /// Shows a row of a table tab (change overview, WP-30): the tab becomes active and focuses the row once it is loaded.
    /// </summary>
    public void ShowRow(TableTab tab, RowChange row)
    {
        tab.FocusRequest = row;
        ActivateTab(tab);
        RequestTabCommand(tab, TabCommand.FocusRow);
    }

    /// <summary>A connection without a password the user wants to open (WP-35): the shell asks for it first.</summary>
    public PasswordRequest? PendingPassword { get; private set; }

    /// <summary>
    /// The page <see cref="Connect"/> came from, once: the lifecycle takes it when it starts to connect, so a later connect
    /// that does not go through <see cref="Connect"/> (app statements, schema comparison) never jumps to an old page.
    /// </summary>
    public ShellPage? TakeConnectOrigin(Guid profileId)
    {
        var origin = _connectOrigin is { } o && o.ProfileId == profileId ? o.Page : (ShellPage?)null;
        _connectOrigin = null;
        return origin;
    }

    /// <summary>
    /// Asks for the password instead of connecting. Through <see cref="Connect"/> (<paramref name="returnTo"/> set) the
    /// shell goes back to the page it left for the explorer; a page that connects on its own stays as it is.
    /// </summary>
    public void RequestPassword(ConnectionProfile profile, ShellPage? returnTo) => Set(() =>
    {
        if (returnTo is { } page)
        {
            Page = page;
        }

        PendingPassword = new PasswordRequest(profile, ShowWhenConnected: returnTo is not null);
    });

    public void ClosePassword() => Set(() => PendingPassword = null);

    /// <summary>The user cancelled the password: a link waiting for that connection (WP-37) does not open later on its own.</summary>
    public void CancelPassword() => Set(() =>
    {
        if (PendingPassword is { } request && _pendingLink?.ConnectionId == request.Profile.Id)
        {
            _pendingLink = null;
        }

        PendingPassword = null;
    });

    /// <summary>A link from outside waiting for the user's confirmation (WP-37); null if none.</summary>
    public IncomingLink? IncomingLink { get; private set; }

    /// <summary>Shows a link from outside for confirmation; a newer one replaces one not yet answered.</summary>
    public void ShowIncomingLink(IncomingLink link) => Set(() => IncomingLink = link);

    public void CloseIncomingLink() => Set(() => IncomingLink = null);

    /// <summary>The import dialog is open (WP-35).</summary>
    public bool ImportOpen { get; private set; }

    public void OpenImport() => Set(() => ImportOpen = true);

    public void CloseImport() => Set(() => ImportOpen = false);

    /// <summary>The export dialog is open (WP-35), for all connections or one group.</summary>
    public ConnectionExportRequest? ExportRequest { get; private set; }

    public void OpenExport(ConnectionExportRequest request) => Set(() => ExportRequest = request);

    public void CloseExport() => Set(() => ExportRequest = null);

    public void OpenSwitcher() => Set(() => SwitcherOpen = true);

    public void CloseSwitcher() => Set(() => SwitcherOpen = false);

    private void Set(Action change)
    {
        change();
        NotifyChanged();
    }
}

/// <summary>Leaving workspaces with uncommitted changes (close, disconnect, switch connection, exit).</summary>
/// <param name="What">What is about to happen, e.g. "Close workspace".</param>
/// <param name="WorkspaceIds">Workspaces whose changes are at stake.</param>
/// <param name="Continue">Runs after the changes were committed or discarded.</param>
public sealed record LeaveRequest(string What, IReadOnlyList<Guid> WorkspaceIds, Func<Task> Continue);

/// <param name="Warnings">Shown below the text, e.g. LOB values that were not exported.</param>
public sealed record Notice(string Text, IReadOnlyList<string> Warnings, DateTimeOffset At);
