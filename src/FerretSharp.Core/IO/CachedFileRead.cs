namespace FerretSharp.Core.IO;

/// <summary>What reading a file gave, and whether it may be reused while the file is unchanged.</summary>
/// <param name="Reuse">False for a read that failed (a file locked for a moment): tried again at the next look.</param>
public readonly record struct FileRead<T>(T Value, bool Reuse = true);

/// <summary>
/// A file that is read again only when it changed (the audit template, the display columns file): within
/// <see cref="Fresh"/> after a look the last result comes back without touching the disk – views ask while they render, and
/// a file on a share over VPN must not stall the UI thread each time; after that the file's time and length decide. R5b:
/// before, a copy each, and the audit template kept a failed read until the file changed.
/// </summary>
/// <param name="read">Reads the file at the path as given, also when it does not exist (<see cref="FileInfo.Exists"/> false).</param>
/// <param name="unreadable">The result when the file cannot even be looked at or read (I/O, access, an invalid path).</param>
public sealed class CachedFileRead<T>(Func<string, FileInfo, FileRead<T>> read, Func<string, Exception, T> unreadable, TimeProvider? clock = null)
{
    /// <summary>How long a look is used without looking at the file again.</summary>
    public static readonly TimeSpan Fresh = TimeSpan.FromSeconds(3);

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Lock _lock = new();
    private (string Path, DateTime Written, long Length, T Value, bool Reuse, DateTimeOffset Checked)? _cached;

    /// <summary>The file at <paramref name="path"/>: the last result if looked at within <see cref="Fresh"/> or unchanged since.</summary>
    public T Get(string path)
    {
        lock (_lock)
        {
            if (_cached is { } recent && recent.Path == path && _clock.GetUtcNow() - recent.Checked < Fresh)
            {
                return recent.Value;
            }
        }

        var (written, length) = (default(DateTime), -1L);
        FileRead<T> result;
        try
        {
            var file = new FileInfo(path);
            if (file.Exists)
            {
                (written, length) = (file.LastWriteTimeUtc, file.Length);
            }

            lock (_lock)
            {
                if (_cached is { Reuse: true } cached && cached.Path == path && (cached.Written, cached.Length) == (written, length))
                {
                    _cached = cached with { Checked = _clock.GetUtcNow() };
                    return cached.Value;
                }
            }

            result = read(path, file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            result = new FileRead<T>(unreadable(path, ex), Reuse: false);
        }

        lock (_lock)
        {
            _cached = (path, written, length, result.Value, result.Reuse, _clock.GetUtcNow());
        }

        return result.Value;
    }

    /// <summary>The next <see cref="Get"/> reads the file (after writing it).</summary>
    public void Forget()
    {
        lock (_lock)
        {
            _cached = null;
        }
    }
}
