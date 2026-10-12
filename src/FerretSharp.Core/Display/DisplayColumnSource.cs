using System.Globalization;
using System.Text.Json;
using FerretSharp.Core.Connections;
using FerretSharp.Core.IO;
using FerretSharp.Core.Resources;
using FerretSharp.Core.Settings;

namespace FerretSharp.Core.Display;

/// <summary>The display columns in use and where they come from.</summary>
/// <param name="Path">The file; it need not exist yet (the first marked column creates it).</param>
/// <param name="IsDefaultPath">No path is set: the file in the data folder.</param>
/// <param name="Error">Why the file cannot be used (unreadable, no valid JSON); the columns are then empty.</param>
/// <param name="ReadOnly">
/// Why the file is not written although it was read (a newer FerretSharp wrote it); null if it can be written.
/// </param>
public sealed record DisplayColumnLoad(DisplayColumns Columns, string Path, bool IsDefaultPath, string? Error, string? ReadOnly = null)
{
    public bool Exists { get; init; }

    /// <summary>The same columns and problems (to skip a reload that changed nothing).</summary>
    public bool SameAs(DisplayColumnLoad other) =>
        Path == other.Path && Error == other.Error && ReadOnly == other.ReadOnly && Columns.SameAs(other.Columns);
}

/// <summary>
/// The display columns of the settings (WP-31, ADR 0022): the file <see cref="AppSettings.DisplayColumnsPath"/> names –
/// e.g. in the company's repository, so colleagues share them – or one in the data folder. Views read the last load
/// (<see cref="Current"/>, no file access on the UI thread); the file is read again when it changes on disk (a
/// <c>git pull</c>, seen by a watcher, and by a regular look for folders that cannot be watched), and
/// <see cref="Changed"/> fires. Marking a column reads the file again first and changes only that entry, so changes of
/// others are kept.
/// </summary>
public sealed class DisplayColumnSource : IDisposable
{
    /// <summary>How long a read of a file is reused without looking at it again (the settings page asks while it renders).</summary>
    public static readonly TimeSpan Fresh = TimeSpan.FromSeconds(3);

    /// <summary>Changes on disk come in bursts (write to a temporary file, move it): reported once after this pause.</summary>
    public static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(300);

    /// <summary>How often the file is looked at besides the watcher (shares that cannot be watched, a watcher that broke).</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);

    private readonly AppSettingsService _settings;
    private readonly string _defaultPath;
    private readonly TimeProvider _clock;
    private readonly Lock _lock = new();

    /// <summary>One look at the file and its report at a time (watcher, poll, settings, writes): never an older read last.</summary>
    private readonly Lock _reporting = new();

    private readonly SemaphoreSlim _writing = new(1, 1);
    private readonly Timer _quiet;
    private readonly Timer? _poll;
    private (string Path, DateTime Written, long Length, DisplayColumnLoad Load, DateTimeOffset Checked)? _cached;
    private (string Path, bool Enabled, DisplayColumnLoad Load) _reported;
    private FileSystemWatcher? _watcher;
    private bool _disposed;

    /// <param name="defaultPath">The file in the data folder (<see cref="AppPaths.DisplayColumnsFile"/>).</param>
    /// <param name="watch">Watch and poll the file for changes on disk (off in tests that call <see cref="Reload"/>).</param>
    public DisplayColumnSource(AppSettingsService settings, string defaultPath, TimeProvider? clock = null, bool watch = true)
    {
        _settings = settings;
        _defaultPath = defaultPath;
        _clock = clock ?? TimeProvider.System;
        _quiet = new Timer(_ => Reload());
        _reported = (PathOf(settings.Current), settings.Current.ShowDisplayNames, Load(PathOf(settings.Current)));
        Watching = watch;
        if (watch)
        {
            Watch(_reported.Path);
            _poll = new Timer(_ => Report(), null, PollInterval, PollInterval);
        }

        _settings.Changed += OnSettingsChanged;
    }

    private bool Watching { get; }

    /// <summary>
    /// The file, the setting to show names or the path changed (not for a look at the file that found nothing new). May
    /// fire on a background thread.
    /// </summary>
    public event Action? Changed;

    /// <summary>Changes whenever <see cref="Changed"/> fires.</summary>
    public int Version { get; private set; }

    /// <summary>Names beside foreign key values are shown at all (the setting).</summary>
    public bool Enabled => _settings.Current.ShowDisplayNames;

    /// <summary>The display columns as last read – no file access (views ask while they render).</summary>
    public DisplayColumnLoad Current()
    {
        lock (_lock)
        {
            return _reported.Load;
        }
    }

    /// <summary>The columns shown beside foreign keys: none if the setting is off.</summary>
    public DisplayColumns Shown => Enabled ? Current().Columns : DisplayColumns.Empty;

    /// <summary>
    /// The file at <paramref name="path"/>, read now unless read within <see cref="Fresh"/> (the settings page checks a path
    /// before it is saved); empty = data folder.
    /// </summary>
    public DisplayColumnLoad Load(string? path)
    {
        path = Resolve(path);
        lock (_lock)
        {
            if (_cached is { } recent && recent.Path == path && _clock.GetUtcNow() - recent.Checked < Fresh)
            {
                return recent.Load;
            }
        }

        var load = Read(path, out var written, out var length);
        lock (_lock)
        {
            _cached = (path, written, length, load, _clock.GetUtcNow());
        }

        return load;
    }

    /// <summary>
    /// Sets the display column of a table (by name, without owner), or removes it with <paramref name="column"/> null.
    /// Reads the file again first, so entries others changed meanwhile are kept.
    /// </summary>
    /// <exception cref="RefusedException">The file cannot be read, is no valid JSON, comes from a newer FerretSharp or cannot be written.</exception>
    public async Task SetAsync(string table, string? column, CancellationToken cancellationToken)
    {
        await _writing.WaitAsync(cancellationToken);
        try
        {
            var path = PathOf(_settings.Current);
            DisplayColumnDocument document;
            try
            {
                // One read: the document written back is the one the change is based on.
                document = await Task.Run(() => ReadDocument(path), cancellationToken) ?? new DisplayColumnDocument();
            }
            catch (JsonException ex)
            {
                throw new RefusedException(TextFormat.Format(DisplayText.FileInvalid, path, ex.Message), ex);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                throw new RefusedException(TextFormat.Format(DisplayText.FileUnreadable, path, ex.Message), ex);
            }

            if (NewerVersion(path, document) is { } readOnly)
            {
                throw new RefusedException(readOnly);
            }

            var columns = DisplayColumns.From(document.DisplayColumns).With(table, column);
            document.Version = DisplayColumnDocument.CurrentVersion;
            document.DisplayColumns = new SortedDictionary<string, string>(columns.All.ToDictionary(), StringComparer.Ordinal);
            try
            {
                await AtomicJsonFile.WriteAsync(path, document, DisplayColumnDocument.Options, cancellationToken);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                throw new RefusedException(TextFormat.Format(DisplayText.FileNotWritten, path, ex.Message), ex);
            }

            if (Watching && _watcher is null)
            {
                Watch(path); // the folder may have been created just now
            }
        }
        finally
        {
            _writing.Release();
        }

        Reload();
    }

    /// <summary>Reads the file again and reports a change (the watcher calls it; tests too).</summary>
    public void Reload()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _cached = null;
        }

        Report();
    }

    private void OnSettingsChanged()
    {
        var path = PathOf(_settings.Current);
        bool moved;
        lock (_lock)
        {
            moved = path != _reported.Path;
        }

        if (moved && Watching)
        {
            Watch(path);
        }

        Report();
    }

    /// <summary>
    /// Looks at the file and raises <see cref="Changed"/> if what views show differs from what was last reported. One at a
    /// time, so a slower, older read never replaces a newer one.
    /// </summary>
    private void Report()
    {
        bool changed;
        lock (_reporting)
        {
            var path = PathOf(_settings.Current);
            var enabled = _settings.Current.ShowDisplayNames;
            var load = Load(path);
            lock (_lock)
            {
                changed = !_disposed && !(_reported.Path == path && _reported.Enabled == enabled && _reported.Load.SameAs(load));
                if (changed)
                {
                    _reported = (path, enabled, load);
                    Version++;
                }
            }
        }

        if (changed)
        {
            Changed?.Invoke();
        }
    }

    private string PathOf(AppSettings settings) => Resolve(settings.DisplayColumnsPath);

    private string Resolve(string? path) => string.IsNullOrWhiteSpace(path) ? _defaultPath : path.Trim();

    /// <summary>The file, parsed – or the earlier load if the file has not changed since (same time and length).</summary>
    private DisplayColumnLoad Read(string path, out DateTime written, out long length)
    {
        (written, length) = (default, -1);
        var isDefault = path == _defaultPath;
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists)
            {
                return new DisplayColumnLoad(DisplayColumns.Empty, path, isDefault, null);
            }

            var stamp = (file.LastWriteTimeUtc, file.Length);
            lock (_lock)
            {
                if (_cached is { } cached && cached.Path == path && (cached.Written, cached.Length) == stamp)
                {
                    (written, length) = stamp;
                    return cached.Load;
                }
            }

            var document = ReadDocument(path) ?? new DisplayColumnDocument();
            (written, length) = stamp; // only a successful read is reused while the file is unchanged
            return new DisplayColumnLoad(DisplayColumns.From(document.DisplayColumns), path, isDefault, null, NewerVersion(path, document)) { Exists = true };
        }
        catch (JsonException ex)
        {
            return new DisplayColumnLoad(DisplayColumns.Empty, path, isDefault, TextFormat.Format(DisplayText.FileInvalid, path, ex.Message)) { Exists = true };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return new DisplayColumnLoad(DisplayColumns.Empty, path, isDefault, TextFormat.Format(DisplayText.FileUnreadable, path, ex.Message));
        }
    }

    private static string? NewerVersion(string path, DisplayColumnDocument document) =>
        document.Version > DisplayColumnDocument.CurrentVersion
            ? TextFormat.Format(DisplayText.FileNewerVersion, path, document.Version.ToString(CultureInfo.InvariantCulture))
            : null;

    /// <summary>The document; null for a missing or empty file.</summary>
    /// <exception cref="JsonException">No valid JSON (or not the expected shape).</exception>
    private static DisplayColumnDocument? ReadDocument(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var text = File.ReadAllText(path);
        return string.IsNullOrWhiteSpace(text) ? null : JsonSerializer.Deserialize<DisplayColumnDocument>(text, DisplayColumnDocument.Options);
    }

    /// <summary>Watches the folder of <paramref name="path"/> for changes of the file; a folder that is missing is not watched.</summary>
    private void Watch(string path)
    {
        FileSystemWatcher? watcher = null;
        try
        {
            var full = System.IO.Path.GetFullPath(path);
            var directory = System.IO.Path.GetDirectoryName(full);
            if (directory is not null && Directory.Exists(directory))
            {
                watcher = new FileSystemWatcher(directory, System.IO.Path.GetFileName(full))
                {
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                };
                watcher.Changed += OnFileChanged;
                watcher.Created += OnFileChanged;
                watcher.Deleted += OnFileChanged;
                watcher.Renamed += OnFileChanged;
                // A share that went away or a buffer overflow: watch anew (the poll covers the time until then).
                watcher.Error += (_, _) =>
                {
                    Touch();
                    Watch(PathOf(_settings.Current));
                };
                watcher.EnableRaisingEvents = true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or PlatformNotSupportedException)
        {
            watcher?.Dispose();
            watcher = null; // a share that cannot be watched: the poll still sees changes
        }

        FileSystemWatcher? old;
        lock (_lock)
        {
            if (_disposed)
            {
                watcher?.Dispose();
                return;
            }

            (old, _watcher) = (_watcher, watcher);
        }

        old?.Dispose();
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e) => Touch();

    private void Touch()
    {
        try
        {
            _quiet.Change(Quiet, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
            // an event that arrived while disposing
        }
    }

    public void Dispose()
    {
        FileSystemWatcher? watcher;
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            (watcher, _watcher) = (_watcher, null);
        }

        _settings.Changed -= OnSettingsChanged;
        watcher?.Dispose();
        _quiet.Dispose();
        _poll?.Dispose();
        _writing.Dispose();
    }
}
