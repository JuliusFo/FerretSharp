using FerretSharp.Core.Data;

namespace FerretSharp.Core.Tests.Data;

public class SnapshotsTests
{
    private static readonly DateTimeOffset S1 = new(2026, 10, 6, 14, 2, 13, TimeSpan.Zero);
    private static readonly DateTimeOffset S2 = S1.AddMinutes(3);

    [Fact]
    public void First_page_sets_the_snapshot_later_pages_in_it_change_nothing()
    {
        var state = Snapshots.Next(0, null, false, S1);
        state = Snapshots.Next(500, state.Shown, state.Moved, S1);

        Assert.Equal((S1, false), state);
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

    [Fact]
    public void Without_snapshots_nothing_moves()
    {
        var state = Snapshots.Next(0, null, false, null);
        state = Snapshots.Next(500, state.Shown, state.Moved, null);

        Assert.Equal((null, false), state);
    }

    [Fact]
    public void A_restored_scroll_position_starts_with_a_later_page()
    {
        var state = Snapshots.Next(1500, null, false, S1);
        state = Snapshots.Next(1000, state.Shown, state.Moved, S1);

        Assert.Equal((S1, false), state);
    }
}
