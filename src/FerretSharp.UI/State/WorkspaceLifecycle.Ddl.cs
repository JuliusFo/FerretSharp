using FerretSharp.Core.Connections;
using FerretSharp.Core.Workspaces;

namespace FerretSharp.UI.State;

// What DDL needs from the lifecycle (WP-22/23): the open transaction it would commit, and the runner every statement
// takes (R4b: SQL editor, designer and LINQ console share it).
public sealed partial class WorkspaceLifecycle
{
    private StatementRunner? _statements;

    /// <summary>Runs statements of the SQL editor, the designer and the LINQ console in a workspace's session (R4b).</summary>
    public StatementRunner Statements => _statements ??= new StatementRunner(shell, hub, editing, this, history, logger);

    /// <summary>
    /// The workspace's open write transaction, which DDL would commit – the confirmation shows what it holds and rolls it
    /// back first; null if there is none. Pending edits are not part of it: DDL leaves them alone (R4a).
    /// </summary>
    public (WorkspaceTabs Workspace, EditSummary Summary)? OpenTransactionOf(Guid workspaceId) =>
        shell.FindWorkspace(workspaceId) is { } workspace && editing.SummaryOf(workspace) is { Transaction.Mode: TransactionMode.ReadWrite } summary
            ? (workspace, summary)
            : null;
}
