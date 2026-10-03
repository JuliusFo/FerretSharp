using FerretSharp.Core.Settings;

namespace FerretSharp.Core.Connections;

/// <summary>
/// Pings idle sessions of the active connection every <see cref="Interval"/> (if <see cref="AppSettings.KeepAlive"/>
/// is on), so a lost connection is reported at once instead of on the next click, and firewalls do not drop idle
/// connections. Sessions that ran a statement within the last <see cref="IdleFor"/> are not pinged.
/// </summary>
public sealed class ConnectionKeepAlive(ActiveConnection active, AppSettingsService settings, TimeProvider? timeProvider = null) : IDisposable
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(2);

    /// <summary>Shorter than <see cref="Interval"/>: a session pinged on the last tick counts as idle again on the next.</summary>
    public static readonly TimeSpan IdleFor = TimeSpan.FromMinutes(1);

    /// <summary>A ping on a dead network can hang; it is cancelled after this and left to the next statement.</summary>
    public static readonly TimeSpan PingTimeout = TimeSpan.FromSeconds(30);

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private ITimer? _timer;
    private int _running;

    /// <summary>A ping found a session gone; may fire on a background thread.</summary>
    public event Action<DatabaseException>? ConnectionLost;

    public void Start() => _timer ??= _time.CreateTimer(_ => _ = TickAsync(), null, Interval, Interval);

    /// <summary>One round of pings (the timer calls it; public for tests). Skipped if the previous one still runs.</summary>
    public async Task TickAsync()
    {
        if (!settings.Current.KeepAlive || !active.IsConnected || Interlocked.Exchange(ref _running, 1) == 1)
        {
            return;
        }

        try
        {
            using var timeout = new CancellationTokenSource(PingTimeout, _time);
            if (await active.PingIdleSessionsAsync(IdleFor, timeout.Token) is { } lost)
            {
                ConnectionLost?.Invoke(lost);
            }
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }

    public void Dispose() => _timer?.Dispose();
}
