using FerretSharp.Core.Connections;
using FerretSharp.Core.Settings;

namespace FerretSharp.UI.State;

/// <summary>Where rows of one connection can be copied to (WP-42): the open workspaces of every other connected connection.</summary>
public static class CopyTargets
{
    /// <summary>In the order of the connections and their workspaces; the source's own connection is left out (decision of the user).</summary>
    public static IReadOnlyList<CopyTarget> Targets(ConnectionHub hub, ActiveConnection source, AppSettings settings) =>
    [
        .. hub.Open
            .Where(scope => !ReferenceEquals(scope.Active, source) && scope.Active.IsConnected && scope.Active.Schema is not null && scope.Profile is not null)
            .SelectMany(scope => scope.Workspaces.Open.Select(workspace => new CopyTarget(
                scope.Id, workspace.Id, $"{ConnectionLabel.Of(scope.Profile!, settings)} · {workspace.Name}", scope.Profile!.Kind,
                () => scope.Workspaces.IsWritable(workspace.Id), scope.Active.Schema!, ct => scope.Workspaces.GetDataAsync(workspace.Id, ct),
                scope.Active))),
    ];
}
