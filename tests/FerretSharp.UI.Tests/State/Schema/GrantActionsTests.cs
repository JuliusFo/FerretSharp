using FerretSharp.UI.State;

namespace FerretSharp.UI.Tests.State;

/// <summary>The grant and revoke actions of the grant views, one at a time (WP-71; review 3.70.0, B6).</summary>
public sealed class GrantActionsTests
{
    /// <summary>The fix of the third pass of WP-71: the synonyms of one object came late and stood under the revoke of another.</summary>
    [Fact]
    public void A_new_action_gives_up_the_read_of_the_one_before()
    {
        using var actions = new GrantActions();
        var first = actions.Next();
        actions.StartReading();

        var second = actions.Next();

        Assert.True(first.IsCancellationRequested);
        Assert.False(second.IsCancellationRequested);
        Assert.False(actions.Reading); // the buttons wait only for the current action's read
    }

    [Fact]
    public void Closing_the_tab_gives_up_the_action_and_one_asked_for_after_it_starts_given_up()
    {
        var actions = new GrantActions();
        var running = actions.Next();

        actions.Dispose();

        Assert.True(running.IsCancellationRequested);
        Assert.True(actions.Next().IsCancellationRequested); // OnRan of a run that ended after the tab closed
        actions.Dispose(); // twice is fine
    }
}
