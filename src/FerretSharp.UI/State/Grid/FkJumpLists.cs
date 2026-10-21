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
    /// Incoming jumps by the table they point to: those into the row's own table (<c>Via</c> null) first, then those through
    /// each base table (<see cref="FkJump.Inherited"/>), in the order the base tables first appear.
    /// </summary>
    public static IReadOnlyList<(TableRef? Via, IReadOnlyList<FkJump> Jumps)> Groups(IReadOnlyList<FkJump> incoming) =>
        [.. incoming.GroupBy(j => j.Inherited?.Via).OrderBy(g => g.Key is not null).Select(g => (g.Key, (IReadOnlyList<FkJump>)[.. g]))];
}
