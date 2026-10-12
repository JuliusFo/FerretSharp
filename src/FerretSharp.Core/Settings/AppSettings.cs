using FerretSharp.Core.Resources;

namespace FerretSharp.Core.Settings;

public enum ThemeMode
{
    /// <summary>Follow the Windows setting (default).</summary>
    System,
    Light,
    Dark,
}

/// <summary>Where the C# model's entity and property names appear next to table and column names (WP-12).</summary>
public enum ClrNameDisplay
{
    /// <summary>Only database names.</summary>
    Off,

    /// <summary>Database name first, the C# name beside it in a subdued style (default).</summary>
    Beside,

    /// <summary>The C# name first, the database name beside it.</summary>
    Front,
}

/// <summary>User preferences of the app (not per connection). Saved as <c>settings.json</c> in the data directory.</summary>
public sealed record AppSettings
{
    public static readonly AppSettings Default = new();

    public ThemeMode Theme { get; init; } = ThemeMode.System;

    /// <summary>Language of the user interface (WP-29); takes effect at the next start. <c>--lang</c> overrides it for a session.</summary>
    public UiLanguage Language { get; init; } = UiLanguage.English;

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

    /// <summary>
    /// Log when the UI thread does not respond for a noticeable time, with what caused it, and the stacks of all threads for
    /// long stalls if the <c>dotnet-stack</c> tool is installed (ADR 0016). Off by default: a tool for finding the cause of
    /// a stall, not for every day.
    /// </summary>
    public bool DiagnoseUiStalls { get; init; }

    /// <summary>Entity and property names of a linked C# project beside the database names (explorer, grid, column search).</summary>
    public ClrNameDisplay ClrNames { get; init; } = ClrNameDisplay.Beside;

    /// <summary>
    /// Shortcuts that differ from the defaults (WP-25): action name → key combination (<c>ctrl+shift+o</c>), empty for
    /// none. Read through <see cref="ShortcutMap"/>.
    /// </summary>
    public ShortcutOverrides Shortcuts { get; init; } = ShortcutOverrides.Empty;

    /// <summary>The change overview (WP-30) is open beside the tabs – it opens again after a restart.</summary>
    public bool ChangesPanelOpen { get; init; }

    /// <summary>Width of the change overview in pixels, as the user dragged it; null for the default.</summary>
    public int? ChangesPanelWidth { get; init; }

    /// <summary>
    /// The audit template (WP-26, <c>docs/audit-templates.md</c>) as a file, e.g. in the company's repository; null or empty
    /// for the built-in one. Read through <see cref="Audit.AuditTemplateSource"/>.
    /// </summary>
    public string? AuditTemplatePath { get; init; }
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

    /// <summary>A setting changed (raised before saving, on the caller's thread).</summary>
    public event Action? Changed;

    /// <summary>
    /// The settings of this session: read from the file, or – if it cannot be read – defaults, with a copy of the file kept
    /// and the user told at start (R4a). Without a copy, nothing is saved in this session, so the file stays as it is.
    /// </summary>
    public static async Task<AppSettingsService> OpenAsync(SettingsStore store, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        try
        {
            return new AppSettingsService(store, await store.LoadAsync(cancellationToken));
        }
        catch (Exception ex) when (ex is SettingsStoreException or IOException or UnauthorizedAccessException)
        {
            try
            {
                var copy = store.KeepCopy(now);
                return new AppSettingsService(store, AppSettings.Default) { StartupNotice = TextFormat.Format(SettingsText.NotReadKept, ex.Message, copy) };
            }
            catch (Exception copyFailed) when (copyFailed is IOException or UnauthorizedAccessException)
            {
                return new AppSettingsService(store, AppSettings.Default)
                {
                    StartupNotice = TextFormat.Format(SettingsText.NotReadNotSaving, ex.Message),
                    NotSaving = SettingsText.NotSavingThisSession,
                };
            }
        }
    }

    /// <summary>What the user should know about the settings file at start (it could not be read, R4a); null if nothing.</summary>
    public string? StartupNotice { get; init; }

    /// <summary>
    /// Why changes are not saved in this session; null if they are. Set when the file could not be read and no copy of it
    /// could be kept: a save would replace it with defaults (R4a).
    /// </summary>
    public string? NotSaving { get; init; }

    /// <summary>Applies the change at once and saves all settings.</summary>
    /// <exception cref="IOException">The file could not be written (or is not, <see cref="NotSaving"/>); the change stays in effect for this session.</exception>
    public async Task UpdateAsync(Func<AppSettings, AppSettings> change, CancellationToken cancellationToken)
    {
        AppSettings snapshot;
        lock (_lock)
        {
            snapshot = _current = change(_current);
        }

        Changed?.Invoke();
        if (NotSaving is { } reason)
        {
            throw new IOException(reason);
        }

        await store.SaveAsync(snapshot, cancellationToken);
    }
}
