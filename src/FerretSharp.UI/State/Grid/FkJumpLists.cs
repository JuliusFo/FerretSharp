using FerretSharp.Core.Query;
using FerretSharp.Core.Schema;

namespace FerretSharp.UI.State;

/// <summary>
/// The FK jumps of a right-click menu by direction, those over the clicked column first – that is usually the one the user
/// means (R6b, review 3.40.0, B5: was written in the grid's cell menu and the result menu).
/// </summary>
public sealed record FkJumpLists(IReadOnlyList<FkJump> Outgoing, IReadOnlyList<FkJump> Incoming)
{
    public static readonly FkJumpLists None = new([], []);

    public int Count => Outgoing.Count + Incoming.Count;

    /// <summary>Headings above the jumps: one per direction and one per base table (<see cref="Groups"/>).</summary>
    public int Headings => (Outgoing.Count > 0 ? 1 : 0) + Groups(Incoming).Count;

    /// <param name="related">Whether a jump goes over the clicked column; the order otherwise stays.</param>
    public static FkJumpLists Of(IEnumerable<FkJump> jumps, Func<FkJump, bool> related)
    {
        var list = jumps.ToList();
        return new(
            [.. list.Where(j => j.Direction == JumpDirection.Outgoing).OrderByDescending(related)],
            [.. Groups(list.Where(j => j.Direction == JumpDirection.Incoming).ToList()).SelectMany(g => g.Jumps.OrderByDescending(related))]);
    }

    /// <summary>
    /// Incoming jumps of one group shown before "… 22 more" (WP-61): the user jumps often, so a long list is folded only well
    /// beyond this.
    /// </summary>
    public const int ShownIncoming = 8;

    /// <summary>
    /// Outgoing jumps without a target (every row's key is NULL, <see cref="FkJump.IsNull"/>) folded into one line ("7 more
    /// are NULL", WP-61) – from two on, and never one over the clicked column: it says why there is no jump.
    /// </summary>
    /// <returns>The jumps shown, and those behind the line (empty if there is none).</returns>
    public static (IReadOnlyList<FkJump> Shown, IReadOnlyList<FkJump> Folded) FoldNull(IReadOnlyList<FkJump> outgoing, Func<FkJump, bool> related)
    {
        bool Folds(FkJump jump) => jump.IsNull && !related(jump);
        var folded = outgoing.Where(Folds).ToList();
        return folded.Count < 2 ? (outgoing, []) : ([.. outgoing.Where(j => !Folds(j))], folded);
    }

    /// <summary>
    /// The first <see cref="ShownIncoming"/> jumps of a group of incoming ones and the rest behind "… 22 more" (WP-61); a
    /// list with at most one more is shown whole – the line would be as long as the entry.
    /// </summary>
    public static (IReadOnlyList<FkJump> Shown, IReadOnlyList<FkJump> Folded) FoldIncoming(IReadOnlyList<FkJump> jumps) =>
        jumps.Count <= ShownIncoming + 1 ? (jumps, []) : ([.. jumps.Take(ShownIncoming)], [.. jumps.Skip(ShownIncoming)]);

    /// <summary>
    /// Incoming jumps by the table they point to: those into the row's own table (<c>Via</c> null) first, then those through
    /// each base table (<see cref="FkJump.Inherited"/>), in the order the base tables first appear.
    /// </summary>
    public static IReadOnlyList<(TableRef? Via, IReadOnlyList<FkJump> Jumps)> Groups(IReadOnlyList<FkJump> incoming) =>
        [.. incoming.GroupBy(j => j.Inherited?.Via).OrderBy(g => g.Key is not null).Select(g => (g.Key, (IReadOnlyList<FkJump>)[.. g]))];
}
