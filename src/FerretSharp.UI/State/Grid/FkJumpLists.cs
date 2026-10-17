using FerretSharp.Core.Query;

namespace FerretSharp.UI.State;

/// <summary>
/// The FK jumps of a right-click menu by direction, those over the clicked column first – that is usually the one the user
/// means (R6b, review 3.40.0, B5: was written in the grid's cell menu and the result menu).
/// </summary>
public sealed record FkJumpLists(IReadOnlyList<FkJump> Outgoing, IReadOnlyList<FkJump> Incoming)
{
    public static readonly FkJumpLists None = new([], []);

    public int Count => Outgoing.Count + Incoming.Count;

    /// <param name="related">Whether a jump goes over the clicked column; the order otherwise stays.</param>
    public static FkJumpLists Of(IEnumerable<FkJump> jumps, Func<FkJump, bool> related)
    {
        var list = jumps.ToList();
        return new(
            [.. list.Where(j => j.Direction == JumpDirection.Outgoing).OrderByDescending(related)],
            [.. list.Where(j => j.Direction == JumpDirection.Incoming).OrderByDescending(related)]);
    }
}
