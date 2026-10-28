using FerretSharp.UI.State;

namespace FerretSharp.UI.Tests.State;

public sealed class ConnectionHubTests : IAsyncDisposable
{
    private readonly TestApp _app = new();

    /// <summary>
    /// Showing opens a scope only once per profile (R3b: on the UI thread; before, a second quick request from the thread
    /// pool could open a second scope for the same connection).
    /// </summary>
    [Fact]
    public void Showing_a_profile_twice_opens_one_scope_and_remembers_the_previous_one()
    {
        var test = TestApp.Profile("Test");
        var dev = TestApp.Profile("Dev");

        var first = _app.Hub.Show(test);
        var again = _app.Hub.Show(test);
        var other = _app.Hub.Show(dev);

        Assert.Same(first, again);
        Assert.Equal([first, other], _app.Hub.Open);
        Assert.True(ConnectionHub.NeedsConnect(first));
        Assert.Same(other, _app.Hub.Current);
        Assert.Same(first, _app.Hub.Previous);
        Assert.True(_app.Hub.ShowPrevious());
        Assert.Same(first, _app.Hub.Current);
    }

    [Fact]
    public async Task A_connected_scope_needs_no_connect()
    {
        var scope = await _app.OpenAsync(TestApp.Profile("Test"));

        Assert.False(ConnectionHub.NeedsConnect(scope));
        Assert.Same(scope, _app.Hub.Show(scope.Profile!));
    }

    /// <summary>
    /// Review of WP-66: a renamed group was looked up under its old name by an open connection (audit template, work clone)
    /// until connecting again. Name, group and paths apply at once; what the session works with needs a new connection.
    /// </summary>
    [Fact]
    public async Task An_open_connection_takes_its_saved_profile_unless_the_session_differs()
    {
        var profile = TestApp.Profile("Test") with { Group = "Kasse" };
        var scope = await _app.OpenAsync(profile);
        var changed = 0;
        scope.Active.Changed += () => changed++;

        _app.Hub.UseSaved([profile with { Group = "Kasse-Neu", Name = "Test 2" }]);

        Assert.Equal("Kasse-Neu", scope.Profile!.Group);
        Assert.Equal("Test 2", scope.Active.Profile!.Name);
        Assert.Equal(1, changed);

        _app.Hub.UseSaved([profile with { Group = "Lager", User = "other" }]);

        Assert.Equal("Kasse-Neu", scope.Profile!.Group); // a new user needs a new connection, the dialog offers it
    }

    public ValueTask DisposeAsync() => _app.DisposeAsync();
}
