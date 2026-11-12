namespace FerretSharp.Core.IO;

/// <summary>
/// A <see cref="FileSystemWatcher"/> as every watcher of the app sets it up (review 3.70.0, B3): file names, folder names,
/// writes and sizes, a 64 KB buffer (two of three had the default 8 KB and overflowed sooner), every change to one handler.
/// Switched on by the caller (<see cref="FileSystemWatcher.EnableRaisingEvents"/>) once it keeps it.
/// </summary>
public static class FileWatch
{
    /// <param name="changed">A file was created, changed, deleted or renamed; called on a thread-pool thread.</param>
    /// <param name="broken">
    /// The watcher failed – its buffer overflowed (something changed for sure) or the share went away. The caller decides
    /// whether to watch anew.
    /// </param>
    /// <exception cref="ArgumentException">The folder does not exist (as <see cref="FileSystemWatcher"/>).</exception>
    public static FileSystemWatcher Create(string directory, string filter, bool subdirectories, Action<FileSystemEventArgs> changed, Action broken)
    {
        ArgumentNullException.ThrowIfNull(changed);
        ArgumentNullException.ThrowIfNull(broken);
        var watcher = new FileSystemWatcher(directory, filter)
        {
            IncludeSubdirectories = subdirectories,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
            InternalBufferSize = 64 * 1024,
        };
        watcher.Changed += (_, e) => changed(e);
        watcher.Created += (_, e) => changed(e);
        watcher.Deleted += (_, e) => changed(e);
        watcher.Renamed += (_, e) => changed(e);
        watcher.Error += (_, _) => broken();
        return watcher;
    }
}
