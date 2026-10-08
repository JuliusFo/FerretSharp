namespace FerretSharp.Core.ClrModel;

/// <summary>
/// Watches a linked project's build output (ADR 0016) and reports once the build has finished writing: a build writes many
/// files, so the callback comes only after <c>quiet</c> without a further change. Without an output yet (not built), it
/// watches the project folder for the first deps.json instead.
/// </summary>
public sealed class BuildOutputWatcher : IDisposable
{
    public static readonly TimeSpan DefaultQuiet = TimeSpan.FromSeconds(1.5);

    private readonly FileSystemWatcher _watcher;
    private readonly Timer _timer;
    private readonly TimeSpan _quiet;
    private readonly bool _depsOnly;
    private int _disposed;

    /// <param name="directory">The output folder, or the project folder if there is no output yet (<paramref name="depsOnly"/>).</param>
    /// <param name="depsOnly">Only a deps.json counts (watching the project folder: obj is written by every design-time build).</param>
    /// <param name="changed">Called on a thread-pool thread.</param>
    public BuildOutputWatcher(string directory, bool depsOnly, TimeSpan quiet, Action changed)
    {
        Directory = directory;
        _quiet = quiet;
        _depsOnly = depsOnly;
        _timer = new Timer(_ =>
        {
            if (Volatile.Read(ref _disposed) == 0)
            {
                changed();
            }
        });
        _watcher = new FileSystemWatcher(directory)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
            InternalBufferSize = 64 * 1024,
        };
        _watcher.Changed += OnChanged;
        _watcher.Created += OnChanged;
        _watcher.Deleted += OnChanged;
        _watcher.Renamed += OnChanged;
        // Too many changes at once (the buffer overflowed): something changed for sure.
        _watcher.Error += (_, _) => Touch();
        _watcher.EnableRaisingEvents = true;
    }

    public string Directory { get; }

    public bool DepsOnly => _depsOnly;

    private void OnChanged(object sender, FileSystemEventArgs e)
    {
        if (!_depsOnly || e.FullPath.EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase) || HasDepsFile(e))
        {
            Touch();
        }
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
