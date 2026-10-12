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
/// e.g. in the company's repository, so colleagues share them – or one in the data folder. Read again when the file
/// changes on disk (a <c>git pull</c>): <see cref="Changed"/> then fires. Marking a column reads the file again first and
/// changes only that entry, so changes of others are kept.
/// </summary>
public sealed class DisplayColumnSource : IDisposable
{
    /// <summary>How long a load is used without looking at the file again (views ask while they render).</summary>
    public static readonly TimeSpan Fresh = TimeSpan.FromSeconds(3);

    /// <summary>Changes on disk come in bursts (write to a temporary file, move it): reported once after this pause.</summary>
    public static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(300);

    private readonly AppSettingsService _settings;
    private readonly string _defaultPath;
    private readonly TimeProvider _clock;
    private readonly Lock _lock = new();
    private readonly SemaphoreSlim _writing = new(1, 1);
    private readonly Timer _quiet;
    private (string Path, DateTime Written, long Length, DisplayColumnLoad Load, DateTimeOffset Checked)? _cached;
    private (string Path, bool Enabled, DisplayColumnLoad Load) _reported;
    private FileSystemWatcher? _watcher;
    private bool _disposed;

    /// <param name="defaultPath">The file in the data folder (<see cref="AppPaths.DisplayColumnsFile"/>).</param>
    /// <param name="watch">Watch the file for changes on disk (off in tests that call <see cref="Reload"/>).</param>
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

    public DisplayColumnLoad Current() => Load(PathOf(_settings.Current));

    /// <summary>The columns shown beside foreign keys: none if the setting is off.</summary>
    public DisplayColumns Shown => Enabled ? Current().Columns : DisplayColumns.Empty;

    /// <summary>The file at <paramref name="path"/> (the settings page checks a path before it is saved); empty = data folder.</summary>
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
            var load = Read(path, out _, out _, useCache: false);
            if (load.Error is { } error)
            {
                throw new RefusedException(error);
            }

            if (load.ReadOnly is { } readOnly)
            {
                throw new RefusedException(readOnly);
            }

            var document = ReadDocument(path) ?? new DisplayColumnDocument();
            var columns = load.Columns.With(table, column);
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

    /// <summary>Raises <see cref="Changed"/> if what views show differs from what was last reported.</summary>
    private void Report()
    {
        var path = PathOf(_settings.Current);
        var enabled = _settings.Current.ShowDisplayNames;
        var load = Load(path);
        lock (_lock)
        {
            if (_disposed || (_reported.Path == path && _reported.Enabled == enabled && _reported.Load.SameAs(load)))
            {
                return;
            }

            _reported = (path, enabled, load);
            Version++;
        }

        Changed?.Invoke();
    }

    private string PathOf(AppSettings settings) => Resolve(settings.DisplayColumnsPath);

    private string Resolve(string? path) => string.IsNullOrWhiteSpace(path) ? _defaultPath : path.Trim();

    /// <summary>The file, parsed – or the earlier load if the file has not changed since (same time and length).</summary>
    private DisplayColumnLoad Read(string path, out DateTime written, out long length, bool useCache = true)
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

            (written, length) = (file.LastWriteTimeUtc, file.Length);
            if (useCache)
            {
                lock (_lock)
                {
                    if (_cached is { } cached && cached.Path == path && cached.Written == written && cached.Length == length)
                    {
                        return cached.Load;
                    }
                }
            }

            var document = ReadDocument(path) ?? new DisplayColumnDocument();
            var readOnly = document.Version > DisplayColumnDocument.CurrentVersion
                ? TextFormat.Format(DisplayText.FileNewerVersion, path, document.Version.ToString(CultureInfo.InvariantCulture))
                : null;
            return new DisplayColumnLoad(DisplayColumns.From(document.DisplayColumns), path, isDefault, null, readOnly) { Exists = true };
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

    /// <summary>The document; null for an empty file.</summary>
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
                watcher.Error += (_, _) => Touch();
                watcher.EnableRaisingEvents = true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or PlatformNotSupportedException)
        {
            watcher?.Dispose();
            watcher = null; // a share that cannot be watched: the file is still read when views ask (Fresh)
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
        _writing.Dispose();
    }
}
