using FerretSharp.Core.Connections;
using FerretSharp.Core.Data;
using FerretSharp.Core.Workspaces;

namespace FerretSharp.UI.State;

/// <summary>
/// Where a table tab reads (WP-78): a tab of now on its workspace's session (it sees the workspace's own changes), a time
/// travel tab on the connection's read session with each table <c>AS OF SCN</c> – never on the workspace's session, whose
/// transaction it must neither see nor wait behind. Everything that reads for a tab (grid, footer, export, copy, form, LOB,
/// FK counts) asks here.
/// </summary>
public static class TabData
{
    public static Task<IDataAccess> GetAsync(TableTab tab, WorkspaceManager workspaces, ActiveConnection active, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tab);
        ArgumentNullException.ThrowIfNull(workspaces);
        ArgumentNullException.ThrowIfNull(active);
        return tab.TimeTravel is { } at ? Task.FromResult(active.Reads.AsOf(at.Scn)) : workspaces.GetDataAsync(tab.WorkspaceId, cancellationToken);
    }

    /// <summary>The same as a delegate for those that take the data access of a workspace (<c>WorkspaceManager.GetDataAsync</c>).</summary>
    public static Func<Guid, CancellationToken, Task<IDataAccess>> Of(TableTab tab, WorkspaceManager workspaces, ActiveConnection active) =>
        (_, cancellationToken) => GetAsync(tab, workspaces, active, cancellationToken);
}
