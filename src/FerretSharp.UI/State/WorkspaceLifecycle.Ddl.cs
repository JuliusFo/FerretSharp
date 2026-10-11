using FerretSharp.Core.Connections;
using FerretSharp.Core.Resources;
using FerretSharp.Core.Workspaces;
using FerretSharp.UI.Resources;
using Microsoft.Extensions.Logging;

namespace FerretSharp.UI.State;

/// <summary>What running DDL from the designer did (WP-23).</summary>
/// <param name="Ran">Statements that ran, in order; DDL is committed at once and stays.</param>
/// <param name="Failed">The statement that failed or was cancelled; the ones after it did not run.</param>
/// <param name="Error">Why it failed; null if all ran or nothing was started.</param>
/// <param name="Lock">The table another session holds, if that is why it failed.</param>
/// <param name="Cancelled">The user cancelled it (the DDL changed nothing).</param>
/// <param name="NotDiscarded">The uncommitted changes could not be rolled back first: nothing ran.</param>
public sealed record DdlRunResult(
    IReadOnlyList<string> Ran,
    string? Failed,
    DatabaseException? Error,
    TableLock? Lock,
    bool Cancelled = false,
    bool NotDiscarded = false)
{
    public bool Succeeded => Failed is null && !NotDiscarded;
}

// Running DDL statements outside the SQL editor (WP-23: index, table designer) – the shared part of what the SQL editor
// does for DDL (WP-22): rollback after confirmation, the schema path statement by statement, history, lock holders, and
// reloading the schema afterwards.
public sealed partial class WorkspaceLifecycle
{
    /// <summary>
    /// The workspace's uncommitted changes (pending or written), which DDL would commit – the confirmation shows them and
    /// rolls them back first; null if there are none.
    /// </summary>
    public (WorkspaceTabs Workspace, EditSummary Summary)? OpenWorkOf(Guid workspaceId) =>
        shell.FindWorkspace(workspaceId) is { } workspace && editing.SummaryOf(workspace) is { HasWork: true } summary ? (workspace, summary) : null;

    /// <summary>
    /// Runs <paramref name="statements"/> one after the other through the schema path of the workspace; the first failure
    /// stops the rest (DDL is not atomic: what ran stays). Afterwards the schema is reloaded and the result shown as a
    /// notice, as after DDL in the SQL editor.
    /// </summary>
    /// <param name="discard">
    /// The workspace's uncommitted changes, which the confirmation said are discarded first (DDL would commit them).
    /// If the rollback does not happen, nothing runs.
    /// </param>
    /// <param name="starting">Called before each statement, for the progress display.</param>
    public async Task<DdlRunResult> RunDdlAsync(Guid workspaceId, IReadOnlyList<string> statements, WorkspaceTabs? discard,
        Action<string>? starting, CancellationToken cancellationToken)
    {
        if (discard is not null && !await editing.RollbackAsync(discard))
        {
            return new DdlRunResult([], null, null, null, NotDiscarded: true);
        }

        var scope = ScopeOf(workspaceId);
        var ran = new List<string>();
        DdlRunResult? failure = null;
        foreach (var statement in statements)
        {
            starting?.Invoke(statement);
            var started = DateTimeOffset.Now;
            var result = await shell.RunDbAsync(logger, scope.Active, () => scope.Workspaces.ExecuteDdlAsync(workspaceId, statement, cancellationToken), cancellationToken);
            if (result.Succeeded)
            {
                ran.Add(statement);
                await LogDdlAsync(scope, statement, started, null);
                continue;
            }

            if (result.Error is not { } error)
            {
                failure = new DdlRunResult(ran, statement, null, null, Cancelled: true);
                break;
            }

            if (!result.IsRefusal)
            {
                await LogDdlAsync(scope, statement, started, error.Display);
            }

            failure = new DdlRunResult(ran, statement, error, await LockOfAsync(scope, statement, error));
            break;
        }

        if (ran.Count > 0)
        {
            var refresh = await RefreshSchemaAsync(scope);
            shell.Notify(refresh.Error is { } error ? TextFormat.Format(SqlEditorText.SqlView_SchemaNotReloaded, error.Display) : SqlEditorText.SqlView_SchemaReloaded,
                refresh.Warnings);
            shell.ReloadTableTabs(workspaceId);
        }

        shell.NotifyChanged(); // status bar: the transaction is gone after a rollback or DDL
        return failure ?? new DdlRunResult(ran, null, null, null);
    }

    /// <summary>Into the connection's SQL history like DDL from the editor, so it can be found and run again elsewhere.</summary>
    private async Task LogDdlAsync(ConnectionScope scope, string statement, DateTimeOffset started, string? error)
    {
        if (scope.Profile is not { } profile)
        {
            return;
        }

        try
        {
            await history.AddAsync(profile.Id, new SqlHistoryEntry(statement, started, DateTimeOffset.Now - started, null, false, false, error, []) { Ddl = error is null },
                CancellationToken.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "SQL history could not be written");
        }
    }
}
