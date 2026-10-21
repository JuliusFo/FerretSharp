using FerretSharp.Core.Connections;
using FerretSharp.Core.Data;
using FerretSharp.Core.Query;
using FerretSharp.Core.Resources;
using FerretSharp.Core.Workspaces;
using FerretSharp.UI.Resources;
using Microsoft.Extensions.Logging;

namespace FerretSharp.UI.State;

/// <summary>What running one statement did (<see cref="StatementRunner"/>).</summary>
/// <param name="At">When it started.</param>
public sealed record StatementOutcome(DateTimeOffset At, TimeSpan Elapsed)
{
    /// <summary>A query: its first page.</summary>
    public SqlPage? FirstPage { get; init; }

    /// <summary>INSERT, UPDATE, DELETE, MERGE: the write in the workspace's transaction.</summary>
    public WriteAction? Written { get; init; }

    /// <summary>Start of the transaction <see cref="Written"/> ran in (tells undo from commit or rollback, <see cref="WorkspaceEditing.FateOf"/>).</summary>
    public DateTimeOffset? Transaction { get; init; }

    /// <summary>
    /// DDL ran: committed by Oracle at once, nothing to undo. Also with <see cref="Error"/> for a trigger that was created
    /// but does not compile (<see cref="TriggerCompileException"/>, WP-26): it exists, disabled.
    /// </summary>
    public bool SchemaChanged { get; init; }

    public DatabaseException? Error { get; init; }

    /// <summary>Refused before it reached the database (<see cref="RefusedException"/>): not marked in the editor, not in the history.</summary>
    public bool Refused { get; init; }

    /// <summary>Cancelled (or its session closed meanwhile): no error.</summary>
    public bool Cancelled { get; init; }

    /// <summary>DDL found the table locked by another session: who holds it.</summary>
    public TableLock? Lock { get; init; }

    /// <summary>The connection's SQL history after the statement went into it; null if it did not.</summary>
    public IReadOnlyList<SqlHistoryEntry>? History { get; init; }
}

/// <summary>How a statement goes into the connection's SQL history (SQL editor, designer); without one it does not.</summary>
/// <param name="Sql">The statement as the user wrote it.</param>
/// <param name="Variables">The variables it uses; on Prod their values are left out.</param>
public sealed record StatementLog(string Sql, IReadOnlyList<SqlVariable> Variables)
{
    public static StatementLog Of(string sql) => new(sql, []);
}

/// <summary>What running DDL from the designer did (WP-23).</summary>
/// <param name="Ran">Statements that ran, in order; DDL is committed at once and stays.</param>
/// <param name="Failed">The statement that failed or was cancelled; the ones after it did not run.</param>
/// <param name="Error">Why it failed; null if all ran or nothing was started.</param>
/// <param name="Lock">The table another session holds, if that is why it failed.</param>
/// <param name="Cancelled">The user cancelled it (the DDL changed nothing).</param>
/// <param name="NotDiscarded">The open transaction could not be rolled back first: nothing ran.</param>
public sealed record DdlRunResult(
    IReadOnlyList<string> Ran,
    string? Failed,
    DatabaseException? Error,
    TableLock? Lock,
    bool Cancelled = false,
    bool NotDiscarded = false)
{
    public bool Succeeded => Failed is null && !NotDiscarded;

    /// <summary>All statements of the run, also those that did not run.</summary>
    public int Total { get; init; }
}

/// <summary>
/// The one way statements run in a workspace's session (R4b) – for the SQL editor, the table designer and the LINQ
/// console, which each did it on their own before: a query reads its first page through the read path; a write goes into
/// the workspace's transaction and the change overview learns its tab; DDL takes the schema path (an open write
/// transaction rolled back first after the confirmation, WP-22), and a table another session holds is named. What ran
/// goes into the SQL history if the caller says how, and afterwards the schema is reloaded and the tabs follow
/// (<see cref="FinishAsync"/>). Failures come back in the outcome, never as exceptions. Owned by the lifecycle.
/// </summary>
public sealed class StatementRunner(
    ShellState shell, ConnectionHub hub, WorkspaceEditing editing, WorkspaceLifecycle lifecycle, SqlHistoryStore history, ILogger logger)
{
    /// <summary>A query: its first page, read here so that cancelling it works (the result grid reads the rest).</summary>
    public async Task<StatementOutcome> ReadAsync(Guid workspaceId, QuerySpec query, int pageSize, StatementLog? log, CancellationToken cancellationToken)
    {
        var scope = hub.ScopeOfWorkspace(workspaceId);
        var at = DateTimeOffset.Now;
        var read = await shell.RunDbAsync(logger, scope.Active, async () =>
            await scope.Workspaces.ReadSqlAsync(workspaceId, query, 0, pageSize, cancellationToken), cancellationToken);
        if (read.Value is not { } page)
        {
            return await FailedAsync(scope, at, read, log, ddl: null);
        }

        var logged = await LogAsync(scope, log, at, page.Elapsed, rows: page.Rows.Count, moreRows: !page.IsLastPage);
        return new StatementOutcome(at, DateTimeOffset.Now - at) { FirstPage = page, History = logged };
    }

    /// <summary>INSERT, UPDATE, DELETE or MERGE in the workspace's transaction; the change overview learns it came from <paramref name="tab"/>.</summary>
    public async Task<StatementOutcome> WriteAsync(WorkspaceTab tab, QuerySpec statement, StatementLog? log, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tab);
        var scope = hub.ScopeOfWorkspace(tab.WorkspaceId);
        var at = DateTimeOffset.Now;

        // A write, commit or rollback of the workspace is running (review 3.40.0, A1): the statement would wait for the
        // editor and land in the commit unseen, or right after the rollback.
        if (editing.IsBusy(tab.WorkspaceId))
        {
            return new StatementOutcome(at, TimeSpan.Zero) { Error = new DatabaseException(GridText.Editing_Busy), Refused = true };
        }

        if (editing.RefusedByExport(tab.WorkspaceId) is { } exporting)
        {
            return new StatementOutcome(at, TimeSpan.Zero) { Error = new DatabaseException(exporting), Refused = true };
        }

        using var running = editing.StatementRunning(tab.WorkspaceId);
        var written = await shell.RunDbAsync(logger, scope.Active, async () =>
            await scope.Workspaces.ExecuteAsync(tab.WorkspaceId, statement, cancellationToken), cancellationToken);
        if (written.Value is not { } action)
        {
            return await FailedAsync(scope, at, written, log, ddl: null);
        }

        editing.NoteStatement(tab, action);
        var logged = await LogAsync(scope, log, at, DateTimeOffset.Now - at, rows: action.Rows, changed: true);
        return new StatementOutcome(at, DateTimeOffset.Now - at)
        {
            Written = action,
            Transaction = scope.Workspaces.TransactionOf(tab.WorkspaceId)?.StartedAt,
            History = logged,
        };
    }

    /// <summary>
    /// One DDL statement through the schema path (WP-22): outside of any transaction, committed by Oracle at once. A table
    /// another session holds is named in <see cref="StatementOutcome.Lock"/>.
    /// </summary>
    public async Task<StatementOutcome> DdlAsync(Guid workspaceId, string statement, StatementLog? log, CancellationToken cancellationToken)
    {
        var scope = hub.ScopeOfWorkspace(workspaceId);
        var at = DateTimeOffset.Now;
        var ran = await shell.RunDbAsync(logger, scope.Active, () => scope.Workspaces.ExecuteDdlAsync(workspaceId, statement, cancellationToken), cancellationToken);
        if (!ran.Succeeded)
        {
            var failed = await FailedAsync(scope, at, ran, log, ddl: statement);

            // WP-26: a trigger that does not compile was created all the same (disabled) – the schema changed.
            return ran.Error is TriggerCompileException ? failed with { SchemaChanged = true } : failed;
        }

        var logged = await LogAsync(scope, log, at, DateTimeOffset.Now - at, ddl: true);
        return new StatementOutcome(at, DateTimeOffset.Now - at) { SchemaChanged = true, History = logged };
    }

    /// <summary>
    /// DDL would commit the workspace's open write transaction: it is rolled back first, as the confirmation said (WP-22).
    /// Pending edits stay (R4a). False if that did not happen – then nothing may run.
    /// </summary>
    public Task<bool> DiscardTransactionAsync(WorkspaceTabs workspace) => editing.RollbackTransactionAsync(workspace);

    /// <summary>
    /// After statements ran: DDL reloads the connection's schema with everything built on it and says so; writes and DDL
    /// reload the table tabs of the workspace (they may show the changed rows) and the status bar (open transaction).
    /// </summary>
    public async Task FinishAsync(Guid workspaceId, bool wrote, bool changedSchema)
    {
        if (changedSchema)
        {
            var refresh = await lifecycle.RefreshSchemaAsync(hub.ScopeOfWorkspace(workspaceId));
            shell.Notify(refresh.Error is { } error ? TextFormat.Format(SqlEditorText.SqlView_SchemaNotReloaded, error.Display) : SqlEditorText.SqlView_SchemaReloaded,
                refresh.Warnings);
        }

        if (wrote || changedSchema)
        {
            shell.ReloadTableTabs(workspaceId);
            shell.NotifyChanged();
        }
    }

    /// <summary>
    /// Runs <paramref name="statements"/> one after the other through the schema path of the workspace (the designer,
    /// WP-23); the first failure stops the rest (DDL is not atomic: what ran stays). Afterwards the schema is reloaded and
    /// the result shown as a notice, as after DDL in the SQL editor.
    /// </summary>
    /// <param name="discard">
    /// The workspace whose open transaction the confirmation said is rolled back first (<see cref="DiscardTransactionAsync"/>).
    /// If the rollback does not happen, nothing runs.
    /// </param>
    /// <param name="starting">Called before each statement, for the progress display.</param>
    /// <param name="ended">
    /// Called with the result once the statements ran, failed or were cancelled – before the schema is reloaded, which may
    /// rebuild the caller's views (WP-23: the designer keeps its draft and the failure in the tab while it still exists;
    /// R4a: after a run that succeeded it drops them, or the rebuilt view reopened with the old failure).
    /// </param>
    public async Task<DdlRunResult> RunDdlAsync(Guid workspaceId, IReadOnlyList<string> statements, WorkspaceTabs? discard,
        Action<string>? starting, Action<DdlRunResult>? ended, CancellationToken cancellationToken)
    {
        // All statements first (R4a): one the schema path refuses would otherwise stop the run after the ones before it
        // were committed – and as a programming error, not as a failure the caller shows.
        if (statements.Select(s => (Statement: s, Rejection: SqlScript.DdlRejection(s))).FirstOrDefault(s => s.Rejection is not null) is ({ } refused, { } why))
        {
            var refusal = new DdlRunResult([], refused, new DatabaseException(why), null) { Total = statements.Count };
            ended?.Invoke(refusal);
            return refusal;
        }

        if (discard is not null && !await DiscardTransactionAsync(discard))
        {
            return new DdlRunResult([], null, null, null, NotDiscarded: true);
        }

        var ran = new List<string>();
        DdlRunResult? failure = null;
        var changedSchema = false;
        foreach (var statement in statements)
        {
            starting?.Invoke(statement);
            var outcome = await DdlAsync(workspaceId, statement, StatementLog.Of(statement), cancellationToken);
            changedSchema |= outcome.SchemaChanged;
            if (outcome is { SchemaChanged: true, Error: null })
            {
                ran.Add(statement);
                continue;
            }

            failure = outcome.Error is { } error
                ? new DdlRunResult(ran, statement, error, outcome.Lock)
                : new DdlRunResult(ran, statement, null, null, Cancelled: true);
            break;
        }

        var result = (failure ?? new DdlRunResult(ran, null, null, null)) with { Total = statements.Count };
        ended?.Invoke(result);
        await FinishAsync(workspaceId, wrote: false, changedSchema);
        shell.NotifyChanged(); // status bar: the transaction is gone after a rollback or DDL
        return result;
    }

    /// <summary>
    /// A failed or cancelled statement: an error Oracle reported goes into the history (a refusal does not – it never ran);
    /// DDL on a table another session holds names who it is.
    /// </summary>
    /// <param name="ddl">The DDL statement that failed; null for a query or a write.</param>
    private async Task<StatementOutcome> FailedAsync<T>(ConnectionScope scope, DateTimeOffset at, DbResult<T> result, StatementLog? log, string? ddl)
    {
        var elapsed = DateTimeOffset.Now - at;
        if (result.Error is not { } error)
        {
            return new StatementOutcome(at, elapsed) { Cancelled = true };
        }

        var logged = result.IsRefusal ? null : await LogAsync(scope, log, at, elapsed, error: error.Display);
        var tableLock = ddl is null ? null : await lifecycle.LockOfAsync(scope, ddl, error);
        return new StatementOutcome(at, elapsed) { Error = error, Refused = result.IsRefusal, Lock = tableLock, History = logged };
    }

    /// <summary>Into the connection's SQL history, so it can be found and run again; on Prod without the values of variables.</summary>
    private async Task<IReadOnlyList<SqlHistoryEntry>?> LogAsync(ConnectionScope scope, StatementLog? log, DateTimeOffset at, TimeSpan elapsed,
        int? rows = null, bool moreRows = false, bool changed = false, string? error = null, bool ddl = false)
    {
        if (log is null || scope.Profile is not { } profile)
        {
            return null;
        }

        // Passwords in the text (IDENTIFIED BY) never go into the history (review 3.31.0, A10).
        var entry = new SqlHistoryEntry(SqlRedaction.Redact(log.Sql), at, elapsed, rows, moreRows, changed, error, log.Variables) { Ddl = ddl };
        try
        {
            return await history.AddAsync(profile.Id, profile.MasksValues ? entry.WithoutValues() : entry, CancellationToken.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "SQL history could not be written");
            return null;
        }
    }
}
