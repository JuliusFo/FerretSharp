using FerretSharp.Core.Links;
using FerretSharp.Core.Query;
using FerretSharp.Core.Resources;
using FerretSharp.UI.Resources;
using FerretSharp.Core.Schema;
using FerretSharp.Core.Workspaces;

namespace FerretSharp.UI.State;

/// <summary>Links and scripts waiting for their connection to be shown (WP-37; PendingOpen since R6b).</summary>
public sealed partial class ShellState
{
    /// <summary>The schema of each connection with workspaces (from <see cref="SyncWorkspaces"/>), to find a linked table.</summary>
    private readonly Dictionary<Guid, SchemaCache> _schemas = [];

    /// <summary>A link waiting for its connection to be shown with its workspaces (<see cref="OpenLinkIn"/>).</summary>
    private readonly PendingOpen<TableLink> _pendingLink = new();

    /// <summary>A script waiting for its connection to be shown with its workspaces (<see cref="OpenSqlIn"/>).</summary>
    private readonly PendingOpen<string> _pendingSql = new();

    private readonly ConnectionFailures _failures = new();

    /// <summary>
    /// Opens the table of a confirmed link (WP-37) in a new tab of the connection's active workspace – right away if the
    /// connection is shown, otherwise once it is (the caller shows or connects it). Only the latest link waits.
    /// </summary>
    public void OpenLinkIn(Guid connectionId, TableLink link)
    {
        _pendingLink.Wait(connectionId, link);
        Set(OpenPendingLink);
    }

    /// <summary>
    /// A connection's failure after each change of it: the error of the last attempt to connect, or why it was lost; null
    /// if none. A new failure drops a link or script waiting for that connection, so it does not open on its own when the
    /// user connects again later (<see cref="ConnectionFailures"/>).
    /// </summary>
    public void ReportConnectionFailure(Guid connectionId, object? failure) => _failures.Report(connectionId, failure, DropPendingFor);

    /// <summary>A link or script waiting for <paramref name="connectionId"/> no longer opens on its own: connecting failed or was cancelled.</summary>
    private void DropPendingFor(Guid connectionId)
    {
        _pendingLink.DropFor(connectionId);
        _pendingSql.DropFor(connectionId);
    }

    /// <summary>What waits for a connection other than <paramref name="shown"/> no longer opens on its own.</summary>
    private void DropPendingExcept(Guid? shown)
    {
        _pendingLink.DropExcept(shown);
        _pendingSql.DropExcept(shown);
    }

    private void OpenPendingLink()
    {
        if (ActiveWorkspace is not { } workspace || CurrentConnection is not { } shown || !_schemas.TryGetValue(shown, out var schema)
            || _pendingLink.Take(shown) is not { } link)
        {
            return;
        }

        if (LinkedTable(schema, link) is not { } table)
        {
            Notice = new Notice(TextFormat.Format(ShellText.Link_TableMissing, link.Table, schema.Owner), [], DateTimeOffset.UtcNow);
            return;
        }

        var tab = new TableTab(workspace.WorkspaceId, table) { AppliedFilters = link.Filters, Sorts = link.Sorts, Mode = link.Mode ?? TabMode.Data, Visited = true, FitToStructure = true };
        tab.SetFilterRows(link.Filters);
        InsertAfterActive(workspace, tab);
    }

    /// <summary>
    /// The linked table on this connection: by owner and name, else by name in the connection's schema (Test and Prod
    /// have the same tables, the schema name may differ), else a synonym of that name.
    /// </summary>
    public static TableSummary? LinkedTable(SchemaCache schema, TableLink link) => LinkedTable(schema, new TableRef(link.Owner, link.Table));

    /// <summary>The table on this connection that matches <paramref name="table"/> of another one, found as a link finds it (also the columns view's script).</summary>
    public static TableSummary? LinkedTable(SchemaCache schema, TableRef table) =>
        schema.Find(table)
        ?? schema.Tables.FirstOrDefault(t => t.Owner == schema.Owner && t.Name == table.Name && t.Synonym is null)
        ?? schema.Tables.FirstOrDefault(t => t.Synonym?.Name == table.Name); // not any other schema's table of that name

    /// <summary>
    /// Opens a SQL tab with <paramref name="text"/> in the active workspace of the connection (WP-22: the DDL proposal of
    /// the schema comparison, to run there) – right away if it is shown, otherwise once it is (the caller shows or
    /// connects it). Only the latest request waits.
    /// </summary>
    public void OpenSqlIn(Guid connectionId, string text)
    {
        _pendingSql.Wait(connectionId, text);
        OpenPendingSql();
    }

    private void OpenPendingSql()
    {
        if (ActiveWorkspace is not null && _pendingSql.Take(CurrentConnection) is { } text)
        {
            OpenSql(text);
        }
    }
}
