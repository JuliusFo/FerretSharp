using FerretSharp.Core.Connections;
using FerretSharp.Core.Data;
using FerretSharp.Core.Query;
using FerretSharp.Core.Resources;
using FerretSharp.Core.Workspaces;
using FerretSharp.UI.Resources;

namespace FerretSharp.UI.State;

/// <summary>Undo and redo of the workspace's writes (WP-30, ADR 0018); R6b (review 3.40.0, B8) moved them here.</summary>
public sealed partial class WorkspaceEditing
{
    /// <summary>
    /// Takes back the last write: rollback to its savepoint. A grid write's changes become pending again; a statement's
    /// rows are as before it (the tabs reload, its result says "undone").
    /// </summary>
    /// <param name="expected">The action the ↶ button named; refused if another one was written meanwhile.</param>
    public Task<bool> UndoLastAsync(WorkspaceTabs workspace, Guid? expected = null) =>
        WorkspacesOf(workspace.WorkspaceId).ActionsOf(workspace.WorkspaceId) is [.., var last]
            ? UndoToAsync(workspace, last.Id, expected ?? last.Id)
            : Task.FromResult(false);

    /// <summary>
    /// Takes back <paramref name="actionId"/> and every later write (WP-30): rollback to its savepoint. Grid writes become
    /// pending again and can be redone; the rows of statements are as before them (the tabs reload, their results say
    /// "undone").
    /// </summary>
    /// <param name="newest">The newest action the user saw; refused if another one was written meanwhile.</param>
    public Task<bool> UndoToAsync(WorkspaceTabs workspace, Guid actionId, Guid newest) => RunAsync(workspace, async () =>
    {
        var id = workspace.WorkspaceId;
        var editor = await WorkspacesOf(id).GetEditorAsync(id, CancellationToken.None);
        var undone = await editor.UndoToAsync(actionId, newest, CancellationToken.None);
        if (undone.Count == 0)
        {
            return false;
        }

        KeepRedoable(id);
        var statements = false;
        var ofClosedTab = false;
        foreach (var action in undone) // newest first, like the rollback
        {
            if (History.TakeBack(id, action.Id) is { } write)
            {
                write.Tab.Changes?.UndoFlush(write.Batch);
                shell.RequestTabCommand(write.Tab, TabCommand.Reload);
                ofClosedTab |= !workspace.Tabs.Contains(write.Tab); // nobody sees its changes pending again (A28)
            }
            else
            {
                statements = true;
            }
        }

        if (statements)
        {
            shell.ReloadTableTabs(id); // any table tab may show rows a statement had changed
        }

        shell.Notify(undone switch
        {
            [{ Kind: WriteActionKind.Grid } single] when ofClosedTab => TextFormat.Format(GridText.Editing_UndoneGridTabClosed, single.Display),
            [{ Kind: WriteActionKind.Grid } single] => TextFormat.Format(GridText.Editing_UndoneGrid, single.Display),
            [var single] => TextFormat.Format(GridText.Editing_Undone, single.Display),
            _ => TextFormat.Format(GridText.Editing_UndoneMany, undone.Count),
        });
        return true;
    });

    /// <summary>Why the next redo cannot run (a grid write whose rows were edited since, or whose tab is closed); null if it can.</summary>
    public string? RedoBlocker(WorkspaceTabs workspace)
    {
        if (WorkspacesOf(workspace.WorkspaceId).UndoneOf(workspace.WorkspaceId) is not [{ Kind: WriteActionKind.Grid } next, ..])
        {
            return null;
        }

        if (History.Undone(workspace.WorkspaceId, next.Id) is not { Tab: { } tab, Batch: { } batch }
            || !workspace.TableTabs.Contains(tab) || tab.Changes is not { } tracker)
        {
            return GridText.Editing_RedoTabClosed;
        }

        return tracker.IsPendingAsUndone(batch) ? null : TextFormat.Format(GridText.Editing_RedoEditedSince, tab.Table.DisplayName);
    }

    /// <summary>
    /// Applies a grid write taken back again (WP-30): writes exactly its pending changes, with the usual lock and concurrency
    /// check (a conflict shows the write problem dialog). Statements are redone through <see cref="RedoStatementAsync"/>,
    /// after the user confirmed.
    /// </summary>
    /// <param name="expected">The write the user chose to redo; refused if it is no longer the next one.</param>
    public Task<bool> RedoGridAsync(WorkspaceTabs workspace, Guid expected) => RunAsync(workspace, async () =>
    {
        var id = workspace.WorkspaceId;
        if (RedoBlocker(workspace) is { } blocker)
        {
            throw new RefusedException(blocker);
        }

        if (WorkspacesOf(id).UndoneOf(id) is not [{ Kind: WriteActionKind.Grid } next, ..] || next.Id != expected
            || History.StartRedo(id, next.Id) is not { } undone)
        {
            throw new RefusedException(DataText.RedoNotPossible);
        }

        var editor = await WorkspacesOf(id).GetEditorAsync(id, CancellationToken.None);
        var tracker = undone.Tab.Changes!;
        var options = new FlushOptions(settings.Current.LockWaitSeconds, Overwrites(id), RedoOf: next.Id);
        if (!await FlushTabAsync(workspace, editor, undone.Tab, tracker.PendingOperations(undone.Batch), options, duringCommit: false))
        {
            History.RedoStopped(id, undone); // still taken back: the problem dialog decides
            return false;
        }

        Overwrites(id).Clear();
        shell.Notify(TextFormat.Format(GridText.Editing_Redone, next.Display));
        return true;
    });

    /// <summary>
    /// Runs a statement taken back again (WP-30), with the same bind values – the caller asked the user first. The data may
    /// have changed meanwhile: the result's <see cref="WriteAction.Rows"/> may differ from the first run's.
    /// </summary>
    /// <returns>The new write; null if it did not run (the failure was shown).</returns>
    public async Task<WriteAction?> RedoStatementAsync(WorkspaceTabs workspace, Guid expected)
    {
        WriteAction? again = null;
        await RunAsync(workspace, async () =>
        {
            var id = workspace.WorkspaceId;
            var editor = await WorkspacesOf(id).GetEditorAsync(id, CancellationToken.None);
            again = await editor.RedoAsync(expected, CancellationToken.None);
            shell.ReloadTableTabs(id); // they may show the rows it changed
            return true;
        });
        return again;
    }

    /// <summary>
    /// Copies rows from another connection into the workspace's transaction (WP-42) as one write; its table tabs reload. Undo
    /// and redo treat it like a statement (redo asks first and compares the rows).
    /// </summary>
    /// <param name="cancellationToken">"Cancel" of the dialog (review 3.54.0, A2): an insert waits without end for a key another
    /// session inserted and has not committed; cancelled, the copy is rolled back to its savepoint.</param>
    /// <returns>The write; null if it did not run (the reason was shown) or was cancelled.</returns>
    public async Task<WriteAction?> CopyAsync(WorkspaceTabs workspace, CopyWrite copy, CancellationToken cancellationToken = default)
    {
        WriteAction? done = null;
        await RunAsync(workspace, async () =>
        {
            var id = workspace.WorkspaceId;
            done = await WorkspacesOf(id).CopyAsync(id, copy, cancellationToken);
            KeepRedoable(id);
            shell.ReloadTableTabs(id);
            return true;
        });
        return done;
    }

    /// <summary>A statement of the SQL editor or the LINQ console was written: the change overview names its tab and jumps there.</summary>
    public void NoteStatement(WorkspaceTab tab, WriteAction action)
    {
        History.NoteStatement(tab, action);
        KeepRedoable(tab.WorkspaceId);
    }

    /// <summary>
    /// A new write ends redo for the writes taken back (except a redo, which keeps the later ones): the history follows the
    /// session's list instead of keeping writes nobody can redo (review 3.31.0, A31).
    /// </summary>
    private void KeepRedoable(Guid workspaceId) =>
        History.KeepRedoable(workspaceId, WorkspacesOf(workspaceId).UndoneOf(workspaceId).Select(a => a.Id));
}
