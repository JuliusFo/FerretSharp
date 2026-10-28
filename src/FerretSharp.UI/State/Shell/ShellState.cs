using FerretSharp.Core.Connections;
using FerretSharp.Core.Data;
using FerretSharp.Core.Query;
using FerretSharp.Core.Schema;
using FerretSharp.Core.Workspaces;

namespace FerretSharp.UI.State;

public enum ConnectionDialogMode { New, Edit, Duplicate }

/// <param name="Group">A new connection starts in this group (WP-66, from the group's menu).</param>
public sealed record ConnectionDialogRequest(ConnectionDialogMode Mode, ConnectionProfile? Profile, string? Group = null);

/// <summary>
/// A group name to type (WP-66): <paramref name="Group"/> renamed, or – without one – a new group for
/// <paramref name="Profile"/>.
/// </summary>
public sealed record GroupNameRequest(string? Group, ConnectionProfile? Profile);

/// <summary>The password to ask for before connecting (WP-35).</summary>
/// <param name="ShowWhenConnected">
/// Asked on the way to the explorer (<c>ShellState.Connect</c>): the connection is shown afterwards. Otherwise the page
/// that connected on its own (app statements, schema comparison) stays.
/// </param>
public sealed record PasswordRequest(ConnectionProfile Profile, bool ShowWhenConnected);

/// <summary>Export all connections, or – <paramref name="OnlyGroup"/> – those of one group (null = without group).</summary>
/// <param name="Connection">Only this connection (WP-66, from its menu); the group is then not looked at.</param>
public sealed record ConnectionExportRequest(bool OnlyGroup = false, string? Group = null, Guid? Connection = null);

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

    /// <summary>
    /// The bind values of <see cref="ErrorDetails"/> stay hidden: the statement ran on a connection that masks values (Prod).
    /// The connection it ran on decides, not the one shown when the dialog opens (review 3.40.0, A8).
    /// </summary>
    public bool ErrorMasked { get; private set; }

    /// <summary>Short-lived message (export done, warnings), shown as a toast.</summary>
    public Notice? Notice { get; private set; }

    /// <summary>Exports written in the background, shown in the status bar (A13, WP-54).</summary>
    public BackgroundReads BackgroundReads { get; } = new();

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

    /// <param name="part">The section to open (WP-66: the audit templates from a group's menu); null: where it was left.</param>
    public void ShowSettings(SettingsPart? part = null) => Set(() =>
    {
        SwitcherOpen = false;
        Page = ShellPage.Settings;
        RequestedSettingsPart = part;
    });

    /// <summary>The section the settings page opens with, once (<see cref="TakeSettingsPart"/>).</summary>
    public SettingsPart? RequestedSettingsPart { get; private set; }

    public SettingsPart? TakeSettingsPart()
    {
        var part = RequestedSettingsPart;
        RequestedSettingsPart = null;
        return part;
    }

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

    /// <param name="group">The group the new connection starts in (WP-66); none by default.</param>
    public void NewConnection(string? group = null) => Set(() => ConnectionDialog = new(ConnectionDialogMode.New, null, group));

    /// <summary>The group name being typed (WP-66): rename a group or put a connection into a new one.</summary>
    public GroupNameRequest? GroupName { get; private set; }

    public void RenameGroup(string group) => Set(() => GroupName = new(group, null));

    public void NewGroupFor(ConnectionProfile profile) => Set(() => GroupName = new(null, profile));

    public void CloseGroupName() => Set(() => GroupName = null);

    public void EditConnection(ConnectionProfile profile) => Set(() => ConnectionDialog = new(ConnectionDialogMode.Edit, profile));

    public void DuplicateConnection(ConnectionProfile profile) => Set(() => ConnectionDialog = new(ConnectionDialogMode.Duplicate, profile));

    public void CloseConnectionDialog() => Set(() => ConnectionDialog = null);

    public void RequestDelete(ConnectionProfile profile) => Set(() => PendingDelete = profile);

    public void CancelDelete() => Set(() => PendingDelete = null);

    /// <summary>An open connection was saved with changes that only a new connection applies (review 3.31.0, A8).</summary>
    public ConnectionProfile? ReconnectOffer { get; private set; }

    public void OfferReconnect(ConnectionProfile profile) => Set(() => ReconnectOffer = profile);

    public void CancelReconnectOffer() => Set(() => ReconnectOffer = null);

    /// <param name="maskValues">The connection the statement ran on masks values (<see cref="ConnectionProfile.MasksValues"/>).</param>
    public void ShowError(DatabaseException error, bool maskValues) => Set(() => (ErrorDetails, ErrorMasked) = (error, maskValues));

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

    /// <summary>
    /// The user cancelled the password: a link (WP-37) or script (review 3.40.0, A17) waiting for that connection does not
    /// open later on its own.
    /// </summary>
    public void CancelPassword() => Set(() =>
    {
        if (PendingPassword is { } request)
        {
            DropPendingFor(request.Profile.Id);
        }

        PendingPassword = null;
    });

    /// <summary>A link from outside waiting for the user's confirmation (WP-37); null if none.</summary>
    public IncomingLink? IncomingLink { get; private set; }

    /// <summary>Shows a link from outside for confirmation (<see cref="TakeLinks"/>: never over one not yet answered).</summary>
    public void ShowIncomingLink(IncomingLink link) => Set(() => IncomingLink = link);

    /// <summary>
    /// Shows the newest waiting link for confirmation (older ones of a burst are dropped: one dialog at a time). While a link
    /// is being confirmed, new ones wait (review 3.40.0, A25): replacing the dialog's content, a click on "Open" meant for
    /// the link just read opened another one. Out of the shell's component, with a test (review 3.44.0, C1).
    /// </summary>
    public void TakeLinks(LinkInbox inbox, IReadOnlyList<ConnectionProfile> profiles)
    {
        if (IncomingLink is not null)
        {
            return;
        }

        string? latest = null;
        while (inbox.TryTake(out var link))
        {
            latest = link;
        }

        if (latest is not null)
        {
            ShowIncomingLink(IncomingLink.Read(latest, profiles));
        }
    }

    public void CloseIncomingLink()
    {
        Set(() => IncomingLink = null);
        IncomingLinkClosed?.Invoke();
    }

    /// <summary>The link dialog was answered: a link that arrived meanwhile is shown now (review 3.40.0, A25).</summary>
    public event Action? IncomingLinkClosed;

    /// <summary>The import dialog is open (WP-35).</summary>
    public bool ImportOpen { get; private set; }

    public void OpenImport() => Set(() => ImportOpen = true);

    public void CloseImport() => Set(() => ImportOpen = false);

    /// <summary>The work clone dialog is open (WP-40): from the status bar or the command palette.</summary>
    public bool WorkClonesOpen { get; private set; }

    public void OpenWorkClones() => Set(() => WorkClonesOpen = true);

    public void CloseWorkClones() => Set(() => WorkClonesOpen = false);

    /// <summary>The export dialog is open (WP-35), for all connections or one group.</summary>
    public ConnectionExportRequest? ExportRequest { get; private set; }

    public void OpenExport(ConnectionExportRequest request) => Set(() => ExportRequest = request);

    public void CloseExport() => Set(() => ExportRequest = null);

    public void OpenSwitcher() => Set(() =>
    {
        SwitcherOpen = true;
        Palette = null;
    });

    public void CloseSwitcher() => Set(() => SwitcherOpen = false);

    /// <summary>
    /// The quick open box is open (WP-38) with this text to start with: empty for the quick open, the command prefix for
    /// the command palette; null if closed. A new value opens it again with that text.
    /// </summary>
    public PaletteRequest? Palette { get; private set; }

    /// <summary>
    /// Opens the quick open box, or closes it if it shows that mode already (the same key again). Showing the other mode,
    /// it opens again in this one.
    /// </summary>
    public void TogglePalette(bool commands) => Set(() =>
    {
        Palette = Palette is { } open && open.ShowsCommands == commands ? null : new PaletteRequest(commands);
        SwitcherOpen = false;
    });

    public void ClosePalette() => Set(() => Palette = null);

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
public sealed record LeaveRequest(string What, IReadOnlyList<Guid> WorkspaceIds, Func<Task> Continue)
{
    /// <summary>
    /// The writes of each workspace when the question was asked: "Commit" on Prod commits nothing written since (review
    /// 3.44.0, A19 – a statement finishing meanwhile was committed unseen, as R4a ruled out for the commit confirmation).
    /// </summary>
    public IReadOnlyDictionary<Guid, IReadOnlyList<WriteAction>> Shown { get; init; } = new Dictionary<Guid, IReadOnlyList<WriteAction>>();
}

/// <param name="Warnings">Shown below the text, e.g. LOB values that were not exported.</param>
public sealed record Notice(string Text, IReadOnlyList<string> Warnings, DateTimeOffset At);
