using System.Diagnostics;

namespace FerretSharp.Testing;

/// <summary>
/// Waits for what another thread makes true – polled every 10 ms, at most <see cref="Limit"/> (review 3.54.0, C11: eight
/// copies of it with limits from 3 to 10 s, some without a message).
/// </summary>
internal static class Wait
{
    public static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    /// <param name="what">Named in the failure: what did not happen.</param>
    public static async Task UntilAsync(Func<bool> condition, string what = "the condition")
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(watch.Elapsed < Limit, $"Timed out waiting for {what}.");
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }
}
