using FerretSharp.Core.Data;

namespace FerretSharp.UI.State;

/// <summary>A grid write of a workspace's transaction: the tab it came from and the rows to make pending again on undo.</summary>
public sealed record GridWrite(Guid ActionId, TableTab Tab, FlushBatch Batch);

/// <summary>
/// The UI's side of a workspace's writes (WP-30, out of <see cref="WorkspaceEditing"/> since R4b): per workspace the grid
/// writes with what to restore when they are undone, the grid writes taken back – their changes are pending again until a
/// redo writes them –, and the tab each statement (SQL editor, LINQ console) ran in. The actions themselves, their
/// savepoints and what can be redone are the session's (<see cref="IDataEditor.Actions"/>, <c>WriteLog</c>); this only
/// remembers what the UI needs next to them. Plain state, no database access.
/// </summary>
public sealed class GridWriteHistory
{
    /// <summary>Per workspace: the grid writes, in the order written.</summary>
    private readonly Dictionary<Guid, List<GridWrite>> _written = [];

    /// <summary>Per workspace: the grid writes taken back, by action id.</summary>
    private readonly Dictionary<Guid, Dictionary<Guid, GridWrite>> _undone = [];

    /// <summary>Per workspace: the tab a statement ran in, by the first write of its line (<see cref="WriteAction.Origin"/>).</summary>
    private readonly Dictionary<Guid, Dictionary<Guid, WorkspaceTab>> _statementTabs = [];

    /// <summary>A grid write of <paramref name="tab"/> became action <paramref name="actionId"/>.</summary>
    public void Written(Guid workspaceId, Guid actionId, TableTab tab, FlushBatch batch) =>
        Get(_written, workspaceId).Add(new GridWrite(actionId, tab, batch));

    /// <summary>
    /// Undo took action <paramref name="actionId"/> back: a grid write moves to the ones taken back and is returned, for its
    /// rows to become pending again; null for a statement.
    /// </summary>
    public GridWrite? TakeBack(Guid workspaceId, Guid actionId)
    {
        if (_written.GetValueOrDefault(workspaceId) is not { } written || written.FindLastIndex(w => w.ActionId == actionId) is not (>= 0 and var index))
        {
            return null;
        }

        var write = written[index];
        written.RemoveAt(index);
        Get(_undone, workspaceId)[actionId] = write;
        return write;
    }

    /// <summary>A grid write taken back; null if the action is no such one (a statement, or forgotten).</summary>
    public GridWrite? Undone(Guid workspaceId, Guid actionId) => _undone.GetValueOrDefault(workspaceId)?.GetValueOrDefault(actionId);

    /// <summary>A redo starts writing <paramref name="actionId"/> again: it is no longer taken back. Null if it is not.</summary>
    public GridWrite? StartRedo(Guid workspaceId, Guid actionId) =>
        _undone.GetValueOrDefault(workspaceId)?.Remove(actionId, out var write) == true ? write : null;

    /// <summary>The redo stopped (the write problem dialog decides): the write is still taken back.</summary>
    public void RedoStopped(Guid workspaceId, GridWrite write) => Get(_undone, workspaceId)[write.ActionId] = write;

    /// <summary>
    /// Writes taken back that a new write made unredoable: their changes simply stay pending – only
    /// <paramref name="redoable"/> stay known.
    /// </summary>
    public void KeepRedoable(Guid workspaceId, IEnumerable<Guid> redoable)
    {
        if (_undone.GetValueOrDefault(workspaceId) is not { } undone)
        {
            return;
        }

        var keep = redoable.ToHashSet();
        foreach (var id in undone.Keys.Where(id => !keep.Contains(id)).ToList())
        {
            undone.Remove(id);
        }
    }

    /// <summary>
    /// The transaction ended without commit (a failed commit, a rollback before DDL, a session given up): every grid write
    /// of the workspace, newest first – to become pending again, as undo would do it. They are forgotten here.
    /// </summary>
    public IReadOnlyList<GridWrite> TakeAll(Guid workspaceId) =>
        _written.Remove(workspaceId, out var written) ? Enumerable.Reverse(written).ToList() : [];

    /// <summary>A statement of the SQL editor or the LINQ console was written in <paramref name="tab"/>.</summary>
    public void NoteStatement(WorkspaceTab tab, WriteAction action)
    {
        ArgumentNullException.ThrowIfNull(tab);
        ArgumentNullException.ThrowIfNull(action);
        Get(_statementTabs, tab.WorkspaceId)[action.Origin] = tab;
    }

    /// <summary>The tab a statement ran in (a redo counts as its first run); null if unknown.</summary>
    public WorkspaceTab? StatementTab(Guid workspaceId, WriteAction action) =>
        _statementTabs.GetValueOrDefault(workspaceId)?.GetValueOrDefault(action.Origin);

    /// <summary>A grid write, written or taken back; null if unknown.</summary>
    public GridWrite? Find(Guid workspaceId, Guid actionId) =>
        _written.GetValueOrDefault(workspaceId)?.FindLast(w => w.ActionId == actionId) ?? Undone(workspaceId, actionId);

    /// <summary>The workspace's transaction ended, or the workspace closed: nothing of it is kept.</summary>
    public void Forget(Guid workspaceId)
    {
        _written.Remove(workspaceId);
        _undone.Remove(workspaceId);
        _statementTabs.Remove(workspaceId);
    }

    /// <summary>Whether the workspace has grid writes in its transaction – also of tabs closed since.</summary>
    public bool HasWritten(Guid workspaceId) => _written.GetValueOrDefault(workspaceId) is { Count: > 0 };

    /// <summary>Whether anything of the workspace is kept (for tests: a closed workspace leaves nothing behind).</summary>
    public bool Knows(Guid workspaceId) =>
        _written.ContainsKey(workspaceId) || _undone.ContainsKey(workspaceId) || _statementTabs.ContainsKey(workspaceId);

    private static TValue Get<TValue>(Dictionary<Guid, TValue> map, Guid workspaceId) where TValue : new() =>
        map.TryGetValue(workspaceId, out var value) ? value : map[workspaceId] = new TValue();
}
