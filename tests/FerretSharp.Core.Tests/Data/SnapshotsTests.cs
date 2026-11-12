using FerretSharp.Core.Data;

namespace FerretSharp.Core.Tests.Data;

public class SnapshotsTests
{
    private static readonly DateTimeOffset S1 = new(2026, 10, 6, 14, 2, 13, TimeSpan.Zero);
    private static readonly DateTimeOffset S2 = S1.AddMinutes(3);

    /// <summary>
    /// The first page sets the snapshot, later pages in it change nothing – also when a restored scroll position starts with
    /// a later page – and without snapshots nothing moves (review 3.70.0, C17: three tests of one call).
    /// </summary>
    [Theory]
    [InlineData(0, 500, true)]
    [InlineData(1500, 1000, true)] // a restored scroll position
    [InlineData(0, 500, false)]
    public void Pages_of_one_snapshot_or_without_snapshots_move_nothing(int first, int second, bool snapshots)
    {
        DateTimeOffset? snapshot = snapshots ? S1 : null;
        var state = Snapshots.Next(first, null, false, snapshot);
        state = Snapshots.Next(second, state.Shown, state.Moved, snapshot);

        Assert.Equal((snapshot, false), state);
    }

    [Fact]
    public void A_later_page_from_another_snapshot_is_noticed_until_the_next_query()
    {
        var state = Snapshots.Next(0, null, false, S1);
        state = Snapshots.Next(500, state.Shown, state.Moved, S2); // another tab started S2 meanwhile
        Assert.Equal((S1, true), state);

        state = Snapshots.Next(1000, state.Shown, state.Moved, S2);
        Assert.Equal((S1, true), state); // stays noticed

        state = Snapshots.Next(0, state.Shown, state.Moved, S2); // F5, new filter: one snapshot again
        Assert.Equal((S2, false), state);
    }
}
