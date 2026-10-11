using FerretSharp.Core.Oracle;

namespace FerretSharp.Core.Data;

/// <summary>The pending state of a row at one point of the edit history; <c>NewRowIndex</c> -1: not listed as a new row.</summary>
internal sealed record RowState(RowChange Change, IReadOnlyDictionary<int, object?> Pending, ChangeStage? Deleted, int NewRowIndex)
{
    /// <summary>A row before an edit created its change: nothing pending, not listed.</summary>
    public static RowState Untouched(RowChange change) => new(change, new Dictionary<int, object?>(), null, -1);

    /// <summary>The same pending values (compared as Oracle values), delete mark and place among the new rows.</summary>
    public bool Same(RowState other) =>
        Deleted == other.Deleted && NewRowIndex == other.NewRowIndex
        && Pending.Count == other.Pending.Count
        && Pending.All(p => other.Pending.TryGetValue(p.Key, out var value) && OracleTypeMapper.ValuesEqual(p.Value, value));
}

/// <summary>
/// The history of a tab's pending edits (WP-30; out of <see cref="ChangeTracker"/> since R4b): each edit is a step from the
/// pending state of the rows it touched before it to the state after it. Undo hands back the states before the last step,
/// redo the states after the last one taken back; the tracker applies them. A new edit ends redo; a write, its undo, commit
/// and rollback end the whole history – the pending state it describes is gone. At most <see cref="MaxSteps"/> steps.
/// </summary>
internal sealed class EditHistory
{
    /// <summary>Steps kept; older ones are dropped.</summary>
    public const int MaxSteps = 500;

    private readonly List<Step> _undo = [];
    private readonly List<Step> _redo = [];

    private sealed record Step(IReadOnlyList<RowState> Before, IReadOnlyList<RowState> After);

    public bool CanUndo => _undo.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    /// <summary>
    /// One edit done: the rows' states before it, and how they are now. An edit that changed nothing is no step; any other
    /// ends redo.
    /// </summary>
    public void Remember(IReadOnlyList<RowState> before, Func<RowChange, RowState> now)
    {
        var step = new Step(before, before.Select(s => now(s.Change)).ToList());
        if (step.Before.Zip(step.After).All(p => p.First.Same(p.Second)))
        {
            return;
        }

        _undo.Add(step);
        if (_undo.Count > MaxSteps)
        {
            _undo.RemoveAt(0);
        }

        _redo.Clear();
    }

    /// <summary>The last edit taken back: the states to restore; empty if there is none.</summary>
    public IReadOnlyList<RowState> Undo() => Move(_undo, _redo, s => s.Before);

    /// <summary>The last edit taken back applied again: the states to restore; empty if there is none.</summary>
    public IReadOnlyList<RowState> Redo() => Move(_redo, _undo, s => s.After);

    /// <summary>A write, its undo, commit or rollback: nothing to step through any more.</summary>
    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
    }

    private static IReadOnlyList<RowState> Move(List<Step> from, List<Step> to, Func<Step, IReadOnlyList<RowState>> target)
    {
        if (from is not [.., var step])
        {
            return [];
        }

        from.RemoveAt(from.Count - 1);
        to.Add(step);
        return target(step);
    }
}
