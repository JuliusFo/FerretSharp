using FerretSharp.UI.State;

namespace FerretSharp.UI.Tests.State;

/// <summary>
/// Review 3.40.0, A14: the connection dialog checked typed paths on the disk in the render – at every keystroke, on the UI
/// thread. The probe answers from a pool thread, once per input, and drops answers for an input typed over since.
/// </summary>
public sealed class PathProbeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Collects the updates the probe hands to the "UI thread"; the test applies them when it wants.</summary>
    private readonly List<Action> _updates = [];

    private readonly Lock _lock = new();
    private readonly SemaphoreSlim _arrived = new(0);

    private void OnUiThread(Action update)
    {
        lock (_lock)
        {
            _updates.Add(update);
        }

        _arrived.Release();
    }

    /// <summary>Waits for <paramref name="count"/> more updates, then applies all that arrived, as the UI thread would.</summary>
    private async Task ApplyUpdatesAsync(int count)
    {
        for (var i = 0; i < count; i++)
        {
            Assert.True(await _arrived.WaitAsync(TimeSpan.FromSeconds(10), Ct));
        }

        lock (_lock)
        {
            foreach (var update in _updates)
            {
                update();
            }

            _updates.Clear();
        }
    }

    [Fact]
    public async Task The_answer_comes_from_the_background_once_per_input()
    {
        var calls = 0;
        var probe = new PathProbe<string, bool>(path => { Interlocked.Increment(ref calls); return path == "a"; }, OnUiThread);

        Assert.Equal((false, false), probe.For("a")); // not known yet
        await ApplyUpdatesAsync(1);

        Assert.Equal((true, true), probe.For("a"));
        Assert.Equal((true, true), probe.For("a"));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task An_answer_for_an_input_typed_over_since_is_dropped()
    {
        var first = new TaskCompletionSource<bool>();
        var probe = new PathProbe<string, bool>(path => path == @"\\s" ? first.Task.Result : true, OnUiThread);

        probe.For(@"\\s");
        Assert.Equal((false, false), probe.For(@"\\se"));
        await ApplyUpdatesAsync(1); // \\se answered
        first.SetResult(false);
        await ApplyUpdatesAsync(1); // \\s answered late

        Assert.Equal((true, true), probe.For(@"\\se"));
    }

    [Fact]
    public async Task Fresh_checks_again_and_waits_for_the_answer()
    {
        var exists = false;
        var probe = new PathProbe<string, bool>(_ => exists, OnUiThread);
        probe.For("p");
        await ApplyUpdatesAsync(1);
        Assert.Equal((true, false), probe.For("p"));

        exists = true;

        Assert.True(await probe.FreshAsync("p"));
        Assert.Equal((true, true), probe.For("p"));
    }

    [Fact]
    public async Task A_check_that_throws_answers_default()
    {
        var probe = new PathProbe<string, string?>(_ => throw new IOException("Netzwerkpfad nicht gefunden"), OnUiThread);

        probe.For("p");
        await ApplyUpdatesAsync(1);

        Assert.Equal((true, null), probe.For("p"));
        Assert.Null(await probe.FreshAsync("p"));
    }
}
