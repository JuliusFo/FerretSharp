using FerretSharp.Core.Connections;

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
        SaveTabs();
        if (hub.Find(profile.Id) is { Lost: not null } lost)
        {
            hub.Show(lost);
            return Task.CompletedTask;
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
    /// "Neu verbinden" after the connection was lost: its transactions are gone, nothing left to confirm. Its old
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

    /// <summary>Disconnects the shown connection (after asking about its uncommitted work); another open one is shown.</summary>
    public Task DisconnectAsync() => hub.Current is { } current ? DisconnectAsync(current) : Task.CompletedTask;

    /// <summary>Disconnects an open connection, shown or not, after asking about its uncommitted work.</summary>
    public Task DisconnectAsync(ConnectionScope scope) =>
        GuardAsync($"Verbindung {scope.Profile?.Name} trennen", WorkspacesOf(scope), () => DisconnectCoreAsync(scope));

    public Task DeleteAsync(ConnectionProfile profile)
    {
        shell.CancelDelete();
        if (hub.Find(profile.Id) is not { } scope)
        {
            return DeleteCoreAsync(profile);
        }

        return GuardAsync("Verbindung löschen", WorkspacesOf(scope), async () =>
        {
            await DisconnectCoreAsync(scope);
            await DeleteCoreAsync(profile);
        });
    }

    /// <summary>Quitting with uncommitted work in any open connection asks first (the window's closing handler, <see cref="ExitGuard"/>).</summary>
    public bool CanExit => editing.WithWork().All(w => IsLost(w.WorkspaceId));

    public Task RequestExitAsync(Action approve) =>
        GuardAsync("FerretSharp beenden", shell.AllWorkspaces, () =>
        {
            approve();
            return Task.CompletedTask;
        });

    private IReadOnlyList<WorkspaceTabs> WorkspacesOf(ConnectionScope scope) => shell.AllWorkspaces.Where(w => w.ConnectionId == scope.Id).ToList();

    private async Task DisconnectCoreAsync(ConnectionScope scope)
    {
        SaveTabs();
        foreach (var workspace in WorkspacesOf(scope))
        {
            editing.Forget(workspace.WorkspaceId);
        }

        await hub.CloseAsync(scope);
        shell.ClearWorkspaces(scope.Id);
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
