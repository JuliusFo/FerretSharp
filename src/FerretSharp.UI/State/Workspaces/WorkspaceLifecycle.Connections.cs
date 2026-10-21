using FerretSharp.Core.Connections;
using FerretSharp.Core.Resources;
using FerretSharp.UI.Resources;

namespace FerretSharp.UI.State;

// The connection-level part of the lifecycle (R3b, a file of its own): showing, reconnecting, disconnecting, deleting a
// connection and quitting – each asking first about the uncommitted work it would drop.
public sealed partial class WorkspaceLifecycle
{
    /// <summary>
    /// Shows the connection, opening it if it is not open yet (WP-24): the connection shown so far stays open in the
    /// background with its workspaces and transactions, so there is nothing to ask. A connection found lost in the
    /// background is only shown – with the banner, so the user sees that its uncommitted work is gone – and connected
    /// again through <see cref="Reconnect"/>; a failed one is connected again right away.
    /// </summary>
    public Task ConnectAsync(ConnectionProfile profile)
    {
        var origin = shell.TakeConnectOrigin(profile.Id);
        SaveTabs();
        if (hub.Find(profile.Id) is { Lost: not null } lost)
        {
            hub.Show(lost);
            return Task.CompletedTask;
        }

        // WP-35: an imported connection has no password yet – ask instead of failing with "no password saved".
        if (hub.Find(profile.Id) is not { } open || ConnectionHub.NeedsConnect(open))
        {
            if (!connections.HasPassword(profile.Id))
            {
                shell.RequestPassword(profile, origin);
                return Task.CompletedTask;
            }
        }

        // Showing (and opening the scope) here on the UI thread; only connecting runs in the background – ActiveConnection
        // reports progress through its Changed event and never throws.
        var scope = hub.Show(profile);
        if (ConnectionHub.NeedsConnect(scope))
        {
            _ = Task.Run(() => scope.Active.ConnectAsync(profile, CancellationToken.None));
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// "Reconnect" after the connection was lost: its transactions are gone, nothing left to confirm. Its old
    /// sessions and workspace tabs are dropped; the workspaces are restored from what was saved.
    /// </summary>
    public void Reconnect(ConnectionProfile profile)
    {
        if (hub.Find(profile.Id) is not { } scope)
        {
            shell.Connect(profile);
            return;
        }

        foreach (var workspace in WorkspacesOf(scope))
        {
            editing.Forget(workspace.WorkspaceId);
        }

        SaveTabs();
        scope.Active.ClearLost();
        shell.ClearWorkspaces(scope.Id);
        hub.Show(scope);
        shell.ShowExplorer();
        _ = Task.Run(() => scope.Active.ConnectAsync(profile, CancellationToken.None));
    }

    /// <summary>
    /// The connection was edited while open (review 3.31.0, A8): disconnect – asking about its uncommitted work first, as
    /// "Disconnect" does – and connect again with the saved profile, so kind, read-only, address, user and schema apply.
    /// </summary>
    public Task ReconnectChangedAsync(ConnectionProfile profile)
    {
        shell.CancelReconnectOffer();
        if (hub.Find(profile.Id) is not { } scope)
        {
            return Task.CompletedTask;
        }

        return GuardAsync(TextFormat.Format(ShellText.Lifecycle_DisconnectWhat, profile.Name), WorkspacesOf(scope), async () =>
        {
            await DisconnectCoreAsync(scope);
            await ConnectAsync(connections.Profiles.FirstOrDefault(p => p.Id == profile.Id) ?? profile);
        });
    }

    /// <summary>Disconnects the shown connection (after asking about its uncommitted work); another open one is shown.</summary>
    public Task DisconnectAsync() => hub.Current is { } current ? DisconnectAsync(current) : Task.CompletedTask;

    /// <summary>Disconnects an open connection, shown or not, after asking about its uncommitted work.</summary>
    public Task DisconnectAsync(ConnectionScope scope) =>
        GuardAsync(TextFormat.Format(ShellText.Lifecycle_DisconnectWhat, scope.Profile?.Name), WorkspacesOf(scope), () => DisconnectCoreAsync(scope));

    public Task DeleteAsync(ConnectionProfile profile)
    {
        shell.CancelDelete();
        if (hub.Find(profile.Id) is not { } scope)
        {
            return DeleteCoreAsync(profile);
        }

        return GuardAsync(ConnectionViewText.Connection_Delete, WorkspacesOf(scope), async () =>
        {
            await DisconnectCoreAsync(scope);
            await DeleteCoreAsync(profile);
        });
    }

    /// <summary>
    /// Quitting with uncommitted work in any open connection, or while exports run (review 3.44.0, A5), asks first (the
    /// window's closing handler, <see cref="ExitGuard"/>).
    /// </summary>
    public bool CanExit => shell.BackgroundReads.Running.Count == 0 && editing.WithWork().All(w => IsLost(w.WorkspaceId));

    /// <summary>Asking before quitting while these exports run: they would be cancelled (review 3.44.0, A5).</summary>
    public IReadOnlyList<BackgroundRead>? ConfirmExitExports { get; private set; }

    private Action? _approveExit;

    public Task RequestExitAsync(Action approve)
    {
        if (shell.BackgroundReads.Running is { Count: > 0 } running)
        {
            (ConfirmExitExports, _approveExit) = (running.ToList(), approve);
            shell.NotifyChanged();
            return Task.CompletedTask;
        }

        return GuardExitAsync(approve);
    }

    private Task GuardExitAsync(Action approve) =>
        GuardAsync(ShellText.Lifecycle_ExitWhat, shell.AllWorkspaces, () =>
        {
            approve();
            return Task.CompletedTask;
        });

    /// <summary>
    /// "Cancel exports and quit": cancels them and waits a moment for their temporary files to go (a read hanging on the VPN
    /// is not waited for), then asks about uncommitted work as usual.
    /// </summary>
    public async Task ExitCancellingExportsAsync()
    {
        if (_approveExit is not { } approve)
        {
            return;
        }

        CancelExitExports();
        await shell.BackgroundReads.CancelAllAsync(ExportsCancelWait);
        await GuardExitAsync(approve); // an export still hanging is not asked about again
    }

    /// <summary>How long quitting waits for cancelled exports to end.</summary>
    internal static readonly TimeSpan ExportsCancelWait = TimeSpan.FromSeconds(3);

    public void CancelExitExports()
    {
        (ConfirmExitExports, _approveExit) = (null, null);
        shell.NotifyChanged();
    }

    private IReadOnlyList<WorkspaceTabs> WorkspacesOf(ConnectionScope scope) => shell.AllWorkspaces.Where(w => w.ConnectionId == scope.Id).ToList();

    private async Task DisconnectCoreAsync(ConnectionScope scope)
    {
        SaveTabs();
        foreach (var workspace in WorkspacesOf(scope))
        {
            editing.Forget(workspace.WorkspaceId);
        }

        await hub.CloseAsync(scope);

        // Opened again while its sessions closed (review 3.31.0, A29): the tabs belong to the new connection now.
        if (hub.Find(scope.Id) is null)
        {
            shell.ClearWorkspaces(scope.Id);
        }

        if (hub.Current is null)
        {
            shell.ShowConnections();
        }
    }

    private async Task DeleteCoreAsync(ConnectionProfile profile)
    {
        await connections.DeleteAsync(profile.Id, CancellationToken.None);
        await hub.Shown.Workspaces.DeleteForConnectionAsync(profile.Id);
        history.Delete(profile.Id);
    }
}
