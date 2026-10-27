namespace FerretSharp.Core.ClrModel;

/// <summary>
/// Watches a linked project's build output (ADR 0016) and reports once the build has finished writing: a build writes many
/// files, so the callback comes only after <c>quiet</c> without a further change. Without an output yet (not built), it
/// watches the project folder for the first deps.json instead. A linked model file (WP-64) is watched the same way: a
/// colleague's new export is copied over it.
/// </summary>
public sealed class BuildOutputWatcher : IDisposable
{
    public static readonly TimeSpan DefaultQuiet = TimeSpan.FromSeconds(1.5);

    private readonly FileSystemWatcher _watcher;
    private readonly ITimer _timer;
    private readonly TimeSpan _quiet;
    private readonly bool _depsOnly;
    private int _disposed;
    private int _seen;
    private int _reported;

    /// <param name="directory">The output folder, or the project folder if there is no output yet (<paramref name="depsOnly"/>).</param>
    /// <param name="depsOnly">Only a deps.json counts (watching the project folder: obj is written by every design-time build).</param>
    /// <param name="changed">Called on a thread-pool thread.</param>
    /// <param name="time">The clock of the quiet period (tests); the system's if null. Changes stay real file events.</param>
    public BuildOutputWatcher(string directory, bool depsOnly, TimeSpan quiet, Action changed, TimeProvider? time = null)
        : this(directory, depsOnly, null, quiet, changed, time)
    {
    }

    private BuildOutputWatcher(string directory, bool depsOnly, string? file, TimeSpan quiet, Action changed, TimeProvider? time)
    {
        Directory = directory;
        File = file;
        _quiet = quiet;
        _depsOnly = depsOnly;
        _timer = (time ?? TimeProvider.System).CreateTimer(_ =>
        {
            if (Volatile.Read(ref _disposed) == 0)
            {
                Interlocked.Increment(ref _reported);
                changed();
            }
        }, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _watcher = new FileSystemWatcher(directory, file ?? "*")
        {
            IncludeSubdirectories = file is null,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
            InternalBufferSize = 64 * 1024,
        };
        _watcher.Changed += OnChanged;
        _watcher.Created += OnChanged;
        _watcher.Deleted += OnChanged;
        _watcher.Renamed += OnChanged;
        // Too many changes at once (the buffer overflowed): something changed for sure.
        _watcher.Error += (_, _) =>
        {
            Touch();
            Interlocked.Increment(ref _seen);
        };
        _watcher.EnableRaisingEvents = true;
    }

    /// <summary>Watches one file (WP-64: a linked model file); it need not exist yet. Its folder must.</summary>
    /// <param name="changed">Called on a thread-pool thread.</param>
    public static BuildOutputWatcher ForFile(string path, TimeSpan quiet, Action changed, TimeProvider? time = null)
    {
        var full = Path.GetFullPath(path);
        return new BuildOutputWatcher(Path.GetDirectoryName(full)!, false, Path.GetFileName(full), quiet, changed, time);
    }

    public string Directory { get; }

    /// <summary>The one file watched in <see cref="Directory"/>; null for a build output.</summary>
    public string? File { get; }

    public bool DepsOnly => _depsOnly;

    /// <summary>File events handled so far, counted or not (tests wait for them before they move the clock on).</summary>
    internal int Seen => Volatile.Read(ref _seen);

    /// <summary>Quiet periods that ended in a report (tests).</summary>
    internal int Reported => Volatile.Read(ref _reported);

    /// <summary>After each file event, counted or not, with its path (tests).</summary>
    internal event Action<string>? Handled;

    private void OnChanged(object sender, FileSystemEventArgs e)
    {
        if (!_depsOnly || e.FullPath.EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase) || HasDepsFile(e))
        {
            Touch();
        }

        Interlocked.Increment(ref _seen);
        Handled?.Invoke(e.FullPath);
    }

    /// <summary>
    /// A folder created by the first build (<c>bin/Debug/net8.0</c>) that already holds a deps.json. On Linux (inotify) a new
    /// subfolder is watched only once its creation was reported; a file written into it before that is never reported.
    /// </summary>
    private static bool HasDepsFile(FileSystemEventArgs e)
    {
        if (e.ChangeType != WatcherChangeTypes.Created || !System.IO.Directory.Exists(e.FullPath))
        {
            return false;
        }

        try
        {
            return System.IO.Directory.EnumerateFiles(e.FullPath, "*.deps.json", SearchOption.AllDirectories).Any();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false; // deleted again or being rewritten: a later event follows
        }
    }

    /// <summary>(Re)starts the quiet period.</summary>
    private void Touch()
    {
        try
        {
            _timer.Change(_quiet, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
            // an event that arrived while disposing
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _watcher.EnableRaisingEvents = false;
        _watcher.Dispose();
        _timer.Dispose();
    }
}
