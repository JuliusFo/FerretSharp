namespace FerretSharp.Core.Settings;

public enum ThemeMode
{
    /// <summary>Follow the Windows setting (default).</summary>
    System,
    Light,
    Dark,
}

/// <summary>User preferences of the app (not per connection). Saved as <c>settings.json</c> in the data directory.</summary>
public sealed record AppSettings
{
    public static readonly AppSettings Default = new();

    public ThemeMode Theme { get; init; } = ThemeMode.System;

    /// <summary>
    /// Ping idle sessions regularly (<see cref="Connections.ConnectionKeepAlive"/>): a lost connection shows up at once,
    /// and firewalls do not drop idle connections. Keeps sessions alive against a database <c>IDLE_TIME</c> as well.
    /// </summary>
    public bool KeepAlive { get; init; } = true;

    /// <summary>
    /// Seconds to wait for a row another session has locked before writing gives up with a lock conflict
    /// (<c>SELECT … FOR UPDATE WAIT n</c>, 1–60).
    /// </summary>
    public int LockWaitSeconds { get; init; } = Query.DmlBuilder.DefaultLockWaitSeconds;
}

/// <summary>
/// The one place that holds the current settings; every change goes through <see cref="UpdateAsync"/>, so changes
/// of different settings (theme, keep-alive) never overwrite each other.
/// </summary>
public sealed class AppSettingsService(SettingsStore store, AppSettings initial)
{
    private readonly Lock _lock = new();
    private AppSettings _current = initial;

    public AppSettings Current
    {
        get
        {
            lock (_lock)
            {
                return _current;
            }
        }
    }

    /// <summary>Applies the change at once and saves all settings.</summary>
    /// <exception cref="IOException">The file could not be written; the change stays in effect for this session.</exception>
    public async Task UpdateAsync(Func<AppSettings, AppSettings> change, CancellationToken cancellationToken)
    {
        AppSettings snapshot;
        lock (_lock)
        {
            snapshot = _current = change(_current);
        }

        await store.SaveAsync(snapshot, cancellationToken);
    }
}
