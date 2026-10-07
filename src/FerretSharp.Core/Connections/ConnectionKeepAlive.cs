using FerretSharp.Core.Settings;

namespace FerretSharp.Core.Connections;

/// <summary>
/// Pings idle sessions of every open connection (WP-24: also those in the background) every <see cref="Interval"/> (if <see cref="AppSettings.KeepAlive"/>
/// is on), so a lost connection is reported at once instead of on the next click, and firewalls do not drop idle
/// connections. Sessions that ran a statement within the last <see cref="IdleFor"/> are not pinged.
/// </summary>
public sealed class ConnectionKeepAlive(IOpenConnections connections, AppSettingsService settings, TimeProvider? timeProvider = null) : IDisposable
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(2);

    /// <summary>Shorter than <see cref="Interval"/>: a session pinged on the last tick counts as idle again on the next.</summary>
    public static readonly TimeSpan IdleFor = TimeSpan.FromMinutes(1);

    /// <summary>A ping on a dead network can hang; it is cancelled after this and left to the next statement.</summary>
    public static readonly TimeSpan PingTimeout = TimeSpan.FromSeconds(30);

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private ITimer? _timer;
    private int _running;

    /// <summary>A ping found a session of this connection gone; may fire on a background thread.</summary>
    public event Action<ActiveConnection, DatabaseException>? ConnectionLost;

    public void Start() => _timer ??= _time.CreateTimer(_ => _ = TickAsync(), null, Interval, Interval);

    /// <summary>One round of pings (the timer calls it; public for tests). Skipped if the previous one still runs.</summary>
    public async Task TickAsync()
    {
        if (!settings.Current.KeepAlive || Interlocked.Exchange(ref _running, 1) == 1)
        {
            return;
        }

        try
        {
            // One after another: a dead VPN would otherwise hang several pings at once; each has its own timeout.
            foreach (var connection in connections.All.Where(c => c.IsConnected).ToList())
            {
                using var timeout = new CancellationTokenSource(PingTimeout, _time);
                if (await connection.PingIdleSessionsAsync(IdleFor, timeout.Token) is { } lost)
                {
                    ConnectionLost?.Invoke(connection, lost);
                }
            }
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }

    public void Dispose() => _timer?.Dispose();
}
