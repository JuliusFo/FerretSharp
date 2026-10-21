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

    public ValueTask DisposeAsync() => _app.DisposeAsync();
}
