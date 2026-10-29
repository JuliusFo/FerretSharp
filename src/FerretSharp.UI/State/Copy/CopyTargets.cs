using FerretSharp.Core.Connections;
using FerretSharp.Core.Data;
using FerretSharp.Core.Relations;
using FerretSharp.Core.Schema;
using FerretSharp.Core.Settings;
using FerretSharp.Core.Workspaces;
using FerretSharp.UI.Resources;

namespace FerretSharp.UI.State;

/// <summary>Where rows of one connection can be copied to (WP-42): the open workspaces of every other connected connection.</summary>
public static class CopyTargets
{
    /// <summary>In the order of the connections and their workspaces; the source's own connection is left out (decision of the user).</summary>
    public static IReadOnlyList<CopyTarget> Targets(ConnectionHub hub, ActiveConnection source, AppSettings settings) =>
    [
        .. hub.Open
            .Where(scope => !ReferenceEquals(scope.Active, source) && scope.Active.IsConnected && scope.Active.Schema is not null && scope.Profile is not null)
            .SelectMany(scope => scope.Workspaces.Open.Select(workspace => Target(scope, workspace.Id, workspace.Name, settings))),
    ];

    private static CopyTarget Target(ConnectionScope scope, Guid workspaceId, string workspaceName, AppSettings settings)
    {
        var atStart = scope.Active.Schema!;
        return new CopyTarget(
            scope.Id, workspaceId, $"{ConnectionLabel.Of(scope.Profile!, settings)} · {workspaceName}", scope.Profile!.Kind,
            () => scope.Workspaces.IsWritable(workspaceId), () => scope.Active.Schema ?? atStart, ct => scope.Workspaces.GetDataAsync(workspaceId, ct),
            scope.Active);
    }

    /// <summary>Why rows of <paramref name="table"/> cannot be copied to another connection (a view, none open); null if they can.</summary>
    public static string? Unavailable(ConnectionHub hub, ActiveConnection source, AppSettings settings, TableDetails? table) =>
        table?.Table.Kind != TableKind.Table ? GridText.GridMenu_CopyToConnectionView
        : Targets(hub, source, settings).Count == 0 ? GridText.GridMenu_CopyToConnectionNone
        : null;

    /// <summary>
    /// The copy of rows of <paramref name="table"/> read on <paramref name="workspaceId"/>'s session into a workspace of another
    /// connection (WP-42): from the cell menu, or from rows marked in a record diagram (WP-60). Null without a schema.
    /// </summary>
    public static SubgraphCopyDraft? Draft(
        ConnectionHub hub, ActiveConnection source, AppSettings settings, WorkspaceManager workspaces, Guid workspaceId, TableDetails table,
        IReadOnlyList<RowKey> keys)
    {
        if (source.Schema is not { } schema || source.Profile is not { } profile)
        {
            return null;
        }

        return new SubgraphCopyDraft(
            ConnectionLabel.Of(profile, settings), schema.Owner, RelationGraph.Of(schema), table, keys,
            ct => workspaces.GetDataAsync(workspaceId, ct),
            async (other, ct) => schema.Find(other) is { } summary ? await schema.GetDetailsAsync(summary, ct) : null,
            Targets(hub, source, settings));
    }
}
