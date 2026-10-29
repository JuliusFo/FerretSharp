using FerretSharp.Core.Resources;

namespace FerretSharp.Core.IO;

/// <summary>What reading a file gave, and whether it may be reused while the file is unchanged.</summary>
/// <param name="Reuse">False for a read that failed (a file locked for a moment): tried again at the next look.</param>
public readonly record struct FileRead<T>(T Value, bool Reuse = true);

/// <summary>
/// A file that is read again only when it changed (the audit template, the display columns file): within
/// <see cref="Fresh"/> after a look the last result comes back without touching the disk – views ask while they render, and
/// a file on a share over VPN must not stall the UI thread each time; after that the file's time and length decide. R5b:
/// before, a copy each, and the audit template kept a failed read until the file changed. Each path is kept on its own
/// (WP-51: the audit templates of several groups are asked for in turn).
/// </summary>
/// <param name="read">Reads the file at the path as given, also when it does not exist (<see cref="FileInfo.Exists"/> false).</param>
/// <param name="unreadable">The result when the file cannot even be looked at or read (I/O, access, an invalid path).</param>
public sealed class CachedFileRead<T>(Func<string, FileInfo, FileRead<T>> read, Func<string, Exception, T> unreadable, TimeProvider? clock = null)
{
    /// <summary>How long a look is used without looking at the file again.</summary>
    public static readonly TimeSpan Fresh = TimeSpan.FromSeconds(3);

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Lock _lock = new();

    /// <summary>Paths kept at most; the one looked at longest ago goes first.</summary>
    internal const int MaxPaths = 32;

    /// <summary>A larger file is not read (a template or a list of names is a few kilobytes).</summary>
    public const long MaxBytes = 4L * 1024 * 1024;

    /// <summary>
    /// What a file larger than <see cref="MaxBytes"/> gives – also for a read that changes the file and writes it back
    /// (display columns, review of R8: that read was not limited).
    /// </summary>
    public static IOException TooLarge() => new(TextFormat.Format(CoreText.FileTooLarge, MaxBytes / (1024 * 1024)));

    private readonly Dictionary<string, (DateTime Written, long Length, T Value, bool Reuse, DateTimeOffset Checked)> _cached = new(StringComparer.Ordinal);

    /// <summary>The file at <paramref name="path"/>: the last result if looked at within <see cref="Fresh"/> or unchanged since.</summary>
    public T Get(string path)
    {
        lock (_lock)
        {
            if (_cached.TryGetValue(path, out var recent) && _clock.GetUtcNow() - recent.Checked < Fresh)
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
                if (_cached.TryGetValue(path, out var cached) && cached.Reuse && (cached.Written, cached.Length) == (written, length))
                {
                    _cached[path] = cached with { Checked = _clock.GetUtcNow() };
                    return cached.Value;
                }
            }

            // Files others write into a shared repository: a huge one (an export saved under the wrong name) was read whole
            // on every change, by the watcher and the poll alike (review 3.54.0, A12). Kept while unchanged.
            result = length > MaxBytes
                ? new FileRead<T>(unreadable(path, TooLarge()))
                : read(path, file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            result = new FileRead<T>(unreadable(path, ex), Reuse: false);
        }

        lock (_lock)
        {
            if (!_cached.ContainsKey(path) && _cached.Count >= MaxPaths)
            {
                _cached.Remove(_cached.MinBy(e => e.Value.Checked).Key);
            }

            _cached[path] = (written, length, result.Value, result.Reuse, _clock.GetUtcNow());
        }

        return result.Value;
    }

    /// <summary>The next <see cref="Get"/> reads the files (after writing one).</summary>
    public void Forget()
    {
        lock (_lock)
        {
            _cached.Clear();
        }
    }
}
