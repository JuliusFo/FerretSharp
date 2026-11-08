using FerretSharp.Core.Connections;
using FerretSharp.Core.Resources;
using FerretSharp.Core.Settings;

namespace FerretSharp.Core.Scripts;

/// <summary>A script file as listed: its place and what its header says.</summary>
/// <param name="Path">Relative to the root, with <c>/</c> (<see cref="ScriptPaths"/>).</param>
/// <param name="Problems">Why the file cannot be opened (too large, unreadable) or header lines with errors; empty if fine.</param>
public sealed record ScriptEntry(
    ScriptRoot Root, string Path, string? Description, IReadOnlyList<ScriptParameter> Parameters, IReadOnlyList<string> Problems,
    DateTime Written, long Length)
{
    /// <summary>The file name without <c>.sql</c>.</summary>
    public string Name => ScriptPaths.NameOf(Path);

    /// <summary>The folder below the root, "" for the root itself.</summary>
    public string Folder => ScriptPaths.FolderOf(Path);

    /// <summary>The same file as listed (records compare lists by reference).</summary>
    public bool SameAs(ScriptEntry other) =>
        Root == other.Root && Path == other.Path && Description == other.Description && Written == other.Written && Length == other.Length
        && Parameters.SequenceEqual(other.Parameters) && Problems.SequenceEqual(other.Problems);
}

/// <summary>What a root holds as last read.</summary>
/// <param name="Folders">Every folder below the root (relative, with <c>/</c>), also empty ones – the save dialog offers them.</param>
/// <param name="Error">Why the folder cannot be read (missing, not absolute, no access); null if it was.</param>
/// <param name="Truncated">More than <see cref="ScriptLibrary.MaxFiles"/> scripts: the rest is not listed.</param>
/// <param name="Loaded">False until the first read finished (the explorer shows "loading").</param>
public sealed record ScriptScan(
    ScriptRoot Root, IReadOnlyList<string> Folders, IReadOnlyList<ScriptEntry> Scripts, string? Error, bool Truncated, bool Loaded = true)
{
    public bool SameAs(ScriptScan other) =>
        Root == other.Root && Error == other.Error && Truncated == other.Truncated && Loaded == other.Loaded
        && Folders.SequenceEqual(other.Folders) && Scripts.Count == other.Scripts.Count
        && Scripts.Zip(other.Scripts).All(p => p.First.SameAs(p.Second));
}

/// <summary>
/// The SQL scripts of all folders of the settings (WP-77, ADR 0046), read in the background: views ask for the last read
/// (<see cref="For"/>, no file access on the UI thread). A folder is read again when something in it changes (a watcher,
/// e.g. after <c>git pull</c>) and every <see cref="PollInterval"/> for folders that cannot be watched; <see cref="Changed"/>
/// fires when what is listed differs. Of each file only the header is read, and only again when it changed.
/// </summary>
public sealed class ScriptLibrary : IDisposable
{
    /// <summary>At most this many scripts per folder are listed.</summary>
    public const int MaxFiles = 2000;

    /// <summary>Of a file only this much is read for its header.</summary>
    internal const int HeaderBytes = 16 * 1024;

    /// <summary>How often the folders are read besides the watchers.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);

    /// <summary>Changes on disk come in bursts (a pull, a save): read once after this pause.</summary>
    public static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(300);

    private readonly AppSettingsService _settings;
    private readonly Lock _lock = new();
    private readonly SemaphoreSlim _reading = new(1, 1);
    private readonly Timer _quiet;
    private readonly Timer? _poll;
    private readonly bool _watch;
    private readonly Dictionary<string, (DateTime Written, long Length, ScriptEntry Entry)> _headers = new(StringComparer.Ordinal);
    private Dictionary<string, ScriptScan> _scans = new(StringComparer.Ordinal);
    private Dictionary<string, FileSystemWatcher> _watchers = new(StringComparer.Ordinal);
    private bool _disposed;

    /// <param name="defaultPersonal">The personal folder in the data folder (<see cref="AppPaths.ScriptsDirectory"/>).</param>
    /// <param name="watch">Watch and poll the folders (off in tests, which call <see cref="RefreshAsync"/>).</param>
    public ScriptLibrary(AppSettingsService settings, string defaultPersonal, bool watch = true)
    {
        _settings = settings;
        DefaultPersonal = defaultPersonal;
        _watch = watch;
        _quiet = new Timer(_ => _ = RefreshQuietlyAsync());
        if (watch)
        {
            _poll = new Timer(_ => _ = RefreshQuietlyAsync(), null, TimeSpan.Zero, PollInterval);
        }

        _settings.Changed += OnSettingsChanged;
    }

    /// <summary>The personal folder when the settings name none.</summary>
    public string DefaultPersonal { get; }

    /// <summary>What is listed changed. May fire on a background thread.</summary>
    public event Action? Changed;

    /// <summary>Changes whenever <see cref="Changed"/> fires.</summary>
    public int Version { get; private set; }

    /// <summary>The folders a connection sees (<see cref="ScriptRoots.For"/>).</summary>
    public IReadOnlyList<ScriptRoot> RootsFor(ConnectionProfile? profile) => ScriptRoots.For(_settings.Current, DefaultPersonal, profile);

    /// <summary>The folders of <paramref name="profile"/> as last read – no file access; one not read yet is "loading".</summary>
    public IReadOnlyList<ScriptScan> For(ConnectionProfile? profile)
    {
        var roots = RootsFor(profile);
        lock (_lock)
        {
            return [.. roots.Select(r => _scans.TryGetValue(Key(r), out var scan) && scan.Root == r ? scan : new ScriptScan(r, [], [], null, false, Loaded: false))];
        }
    }

    /// <summary>A folder as last read (the settings page shows what it holds); "loading" if not read yet.</summary>
    public ScriptScan ScanOf(ScriptRoot root)
    {
        lock (_lock)
        {
            return _scans.TryGetValue(Key(root), out var scan) && scan.Root == root ? scan : new ScriptScan(root, [], [], null, false, Loaded: false);
        }
    }

    /// <summary>The script at <paramref name="path"/> of <paramref name="root"/> as last read; null if not listed.</summary>
    public ScriptEntry? Find(ScriptRoot root, string path)
    {
        lock (_lock)
        {
            return _scans.TryGetValue(Key(root), out var scan)
                ? scan.Scripts.FirstOrDefault(s => string.Equals(s.Path, path, StringComparison.OrdinalIgnoreCase))
                : null;
        }
    }

    /// <summary>Reads all folders now (after a save or delete; tests) and fires <see cref="Changed"/> if something differs.</summary>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        await _reading.WaitAsync(cancellationToken);
        bool changed;
        try
        {
            var roots = ScriptRoots.All(_settings.Current, DefaultPersonal);
            var scans = await Task.Run(() => roots.Select(Read).ToList(), cancellationToken);
            lock (_lock)
            {
                if (_disposed)
                {
                    return;
                }

                var next = new Dictionary<string, ScriptScan>(StringComparer.Ordinal);
                foreach (var scan in scans)
                {
                    next.TryAdd(Key(scan.Root), scan);
                }

                // Headers of files no longer listed go (a folder renamed in a pull leaves all its files behind).
                var listed = scans.SelectMany(scan => scan.Scripts.Select(e => Key(scan.Root) + "|" + e.Path)).ToHashSet(StringComparer.Ordinal);
                foreach (var stale in _headers.Keys.Where(k => !listed.Contains(k)).ToList())
                {
                    _headers.Remove(stale);
                }

                changed = next.Count != _scans.Count
                          || next.Any(e => !_scans.TryGetValue(e.Key, out var old) || !old.SameAs(e.Value));
                _scans = next;
                if (changed)
                {
                    Version++;
                }
            }

            if (_watch)
            {
                Watch(roots);
            }
        }
        finally
        {
            _reading.Release();
        }

        if (changed)
        {
            Changed?.Invoke();
        }
    }

    private async Task RefreshQuietlyAsync()
    {
        try
        {
            await RefreshAsync();
        }
        catch (ObjectDisposedException)
        {
            // disposed while a timer fired
        }
    }

    /// <summary>One root: its folders and scripts, sorted by name; never throws.</summary>
    private ScriptScan Read(ScriptRoot root)
    {
        if (!Path.IsPathFullyQualified(root.Path))
        {
            return new ScriptScan(root, [], [], TextFormat.Format(ScriptText.RootNotAbsolute, root.Path), false);
        }

        try
        {
            if (!Directory.Exists(root.Path))
            {
                // The personal folder is created by the first save; a shared one that is missing is a wrong path.
                return new ScriptScan(root, [], [], root.Kind == ScriptRootKind.Personal ? null : TextFormat.Format(ScriptText.FolderMissing, root.Path), false);
            }

            var folders = new List<string>();
            var scripts = new List<ScriptEntry>();
            var truncated = false;
            var pending = new Stack<(DirectoryInfo Directory, string Relative, int Depth)>();
            pending.Push((new DirectoryInfo(root.Path), "", 0));
            var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System };
            while (pending.Count > 0 && !truncated)
            {
                var (directory, relative, depth) = pending.Pop();
                foreach (var info in directory.EnumerateFileSystemInfos("*", options))
                {
                    // Hidden by name (.git), links (they may lead out of the folder) and names a save would refuse are not listed.
                    if (ScriptPaths.NameProblem(info.Name) is not null || info.LinkTarget is not null)
                    {
                        continue;
                    }

                    var path = relative.Length == 0 ? info.Name : relative + "/" + info.Name;
                    if (info is DirectoryInfo sub)
                    {
                        if (depth < ScriptPaths.MaxDepth)
                        {
                            folders.Add(path);
                            pending.Push((sub, path, depth + 1));
                        }
                    }
                    else if (info is FileInfo file && file.Extension.Equals(ScriptPaths.Extension, StringComparison.OrdinalIgnoreCase))
                    {
                        if (scripts.Count >= MaxFiles)
                        {
                            truncated = true;
                            break;
                        }

                        scripts.Add(Entry(root, path, file));
                    }
                }
            }

            var byName = StringComparer.CurrentCultureIgnoreCase;
            return new ScriptScan(
                root, [.. folders.Order(byName)], [.. scripts.OrderBy(s => s.Folder, byName).ThenBy(s => s.Name, byName)], null, truncated);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return new ScriptScan(root, [], [], TextFormat.Format(ScriptText.FolderUnreadable, root.Path, ex.Message), false);
        }
    }

    /// <summary>The header of a file, read again only when its time or length changed.</summary>
    private ScriptEntry Entry(ScriptRoot root, string path, FileInfo file)
    {
        var key = Key(root) + "|" + path;
        var (written, length) = (file.LastWriteTimeUtc, file.Length);
        lock (_lock)
        {
            if (_headers.TryGetValue(key, out var cached) && cached.Written == written && cached.Length == length && cached.Entry.Root == root)
            {
                return cached.Entry;
            }
        }

        ScriptEntry entry;
        if (length > ScriptFiles.MaxBytes)
        {
            entry = new ScriptEntry(root, path, null, [], [TextFormat.Format(ScriptText.FileTooLarge, path, ScriptFiles.MaxBytes / 1024)], written, length);
        }
        else
        {
            try
            {
                using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var buffer = new byte[(int)Math.Min(length, HeaderBytes)];
                stream.ReadExactly(buffer);
                var parsed = ScriptHeaders.Parse(ScriptFiles.Decode(buffer).Text);
                entry = new ScriptEntry(root, path, parsed.Header.Description, parsed.Header.Parameters, [.. parsed.Errors.Select(e => e.ToString())], written, length);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A file locked for a moment: read again at the next look (not kept).
                return new ScriptEntry(root, path, null, [], [TextFormat.Format(ScriptText.FileUnreadable, path, ex.Message)], written, length);
            }
        }

        lock (_lock)
        {
            _headers[key] = (written, length, entry);
        }

        return entry;
    }

    private void OnSettingsChanged() => _ = RefreshQuietlyAsync();

    /// <summary>Watches each root that exists, with its subfolders; watchers of roots no longer set go.</summary>
    private void Watch(IReadOnlyList<ScriptRoot> roots)
    {
        var wanted = roots.Select(r => r.Path).Where(p => Path.IsPathFullyQualified(p) && Directory.Exists(p)).Distinct(StringComparer.Ordinal).ToList();
        List<FileSystemWatcher> gone;
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            gone = [.. _watchers.Where(w => !wanted.Contains(w.Key)).Select(w => w.Value)];
            var kept = _watchers.Where(w => wanted.Contains(w.Key)).ToDictionary(w => w.Key, w => w.Value, StringComparer.Ordinal);
            foreach (var path in wanted.Where(p => !kept.ContainsKey(p)))
            {
                if (Watcher(path) is { } watcher)
                {
                    kept[path] = watcher;
                }
            }

            _watchers = kept;
        }

        gone.ForEach(w => w.Dispose());
    }

    private FileSystemWatcher? Watcher(string path)
    {
        try
        {
            var watcher = new FileSystemWatcher(path)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
            };
            watcher.Changed += OnFileChanged;
            watcher.Created += OnFileChanged;
            watcher.Deleted += OnFileChanged;
            watcher.Renamed += OnFileChanged;
            watcher.Error += (_, _) => Touch(); // the poll covers the time until the next read sets it up again
            watcher.EnableRaisingEvents = true;
            return watcher;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or PlatformNotSupportedException)
        {
            return null; // a share that cannot be watched: the poll still sees changes
        }
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

    private static string Key(ScriptRoot root) => root.Kind + "|" + root.Group + "|" + root.Path;

    public void Dispose()
    {
        List<FileSystemWatcher> watchers;
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            watchers = [.. _watchers.Values];
            _watchers.Clear();
        }

        _settings.Changed -= OnSettingsChanged;
        watchers.ForEach(w => w.Dispose());
        _quiet.Dispose();
        _poll?.Dispose();
        _reading.Dispose();
    }
}
