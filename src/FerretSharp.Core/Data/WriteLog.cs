using FerretSharp.Core.Connections;
using FerretSharp.Core.Resources;

namespace FerretSharp.Core.Data;

/// <summary>
/// The writes of one transaction with their savepoints, and the writes taken back that can be applied again (WP-30) –
/// the bookkeeping of the undo and redo stacks without the session work. Taking back an action rolls back to its
/// savepoint, so every later action goes with it (a savepoint cannot be rolled back selectively); they become redoable,
/// the oldest first. Any write that is not the next redo ends redo, and so do commit and rollback (<see cref="Clear"/>).
/// <para>
/// Not thread-safe for writers (the editor writes one at a time); readers on other threads see consistent lists, which
/// are replaced, never changed.
/// </para>
/// </summary>
public sealed class WriteLog
{
    private sealed record Entry(WriteAction Action, string Savepoint);

    private volatile Entry[] _done = [];
    private volatile WriteAction[] _undone = [];

    /// <summary>The writes of the transaction, oldest first.</summary>
    public IReadOnlyList<WriteAction> Actions => _done.Select(e => e.Action).ToList();

    /// <summary>Writes taken back that can be applied again, the next one first.</summary>
    public IReadOnlyList<WriteAction> Undone => _undone;

    /// <summary>The write the next redo applies again; null if there is none.</summary>
    public WriteAction? NextRedo => _undone.FirstOrDefault();

    /// <summary>The transaction ended (commit, rollback) or a new one begins.</summary>
    public void Clear()
    {
        _done = [];
        _undone = [];
    }

    /// <summary>
    /// A write behind <paramref name="savepoint"/>. A redo (<see cref="WriteAction.RedoOf"/>) leaves the writes after the
    /// one it applies again redoable; any other write ends redo.
    /// </summary>
    public void Add(WriteAction action, string savepoint)
    {
        _undone = action.RedoOf is { } origin && _undone is [var next, .. var rest] && next.Origin == origin ? rest : [];
        _done = [.. _done, new Entry(action, savepoint)];
    }

    /// <summary>
    /// Refuses a redo of <paramref name="actionId"/> unless it is the next one; returns the write to apply again.
    /// </summary>
    public WriteAction CheckRedo(Guid actionId) =>
        NextRedo is { } next && next.Id == actionId
            ? next
            : throw new RefusedException(DataText.RedoNotPossible);

    /// <summary>
    /// The savepoint to roll back to for taking back <paramref name="actionId"/> and everything after it – refused if the
    /// action is no longer open, or if <paramref name="newest"/> is no longer the newest (it would be taken back unseen).
    /// </summary>
    public string SavepointFor(Guid actionId, Guid newest)
    {
        var done = _done;
        var index = Array.FindIndex(done, e => e.Action.Id == actionId);
        if (index < 0)
        {
            throw new RefusedException(DataText.ActionNoLongerOpen);
        }

        if (done[^1].Action.Id != newest)
        {
            throw new RefusedException(TextFormat.Format(DataText.WrittenMeanwhile, done[^1].Action.Display));
        }

        return done[index].Savepoint;
    }

    /// <summary>
    /// After the rollback to the savepoint of <paramref name="actionId"/>: it and the later actions are gone from
    /// <see cref="Actions"/> and the next redos, the oldest first.
    /// </summary>
    /// <returns>The actions taken back, newest first.</returns>
    public IReadOnlyList<WriteAction> TakeBack(Guid actionId)
    {
        var index = Array.FindIndex(_done, e => e.Action.Id == actionId);
        if (index < 0)
        {
            return [];
        }

        var taken = _done[index..].Select(e => e.Action).ToArray();
        _done = _done[..index];
        _undone = [.. taken, .. _undone];
        return taken.Reverse().ToList();
    }
}
