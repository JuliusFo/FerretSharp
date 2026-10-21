using Microsoft.Extensions.Time.Testing;

namespace FerretSharp.UI.Tests;

/// <summary>
/// A fake clock that counts the timers created on it, i.e. the delays started. A check that "nothing happens yet" waits
/// until the code is parked on its timer instead of for a fixed time (review 3.40.0, C8: a fixed wait passed under load
/// also when something happened too early).
/// </summary>
internal sealed class CountingTimeProvider : FakeTimeProvider
{
    private int _timers;

    public int Timers => Volatile.Read(ref _timers);

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = base.CreateTimer(callback, state, dueTime, period);
        Interlocked.Increment(ref _timers);
        return timer;
    }
}
