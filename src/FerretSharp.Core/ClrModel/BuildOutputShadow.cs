using System.Security.Cryptography;
using System.Text;

namespace FerretSharp.Core.ClrModel;

/// <summary>Size and write time of a file: what "unchanged" means for a build output.</summary>
public readonly record struct FileStamp(long Length, long WriteTicks)
{
    public static FileStamp Of(FileInfo file) => new(file.Length, file.LastWriteTimeUtc.Ticks);
}

/// <summary>The files of a build output folder, subfolders included (relative path → stamp).</summary>
public static class BuildFiles
{
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>Empty if the folder does not exist.</summary>
    public static IReadOnlyDictionary<string, FileStamp> List(string directory)
    {
        var files = new Dictionary<string, FileStamp>(PathComparer);
        var root = new DirectoryInfo(directory);
        if (!root.Exists)
        {
            return files;
        }

        foreach (var file in root.EnumerateFiles("*", SearchOption.AllDirectories))
        {
            files[Path.GetRelativePath(root.FullName, file.FullName)] = FileStamp.Of(file);
        }

        return files;
    }

    public static bool Same(IReadOnlyDictionary<string, FileStamp> a, IReadOnlyDictionary<string, FileStamp> b) =>
        a.Count == b.Count && a.All(f => b.TryGetValue(f.Key, out var stamp) && stamp == f.Value);
}

/// <summary>A copy of a build output a model host runs from; disposing it frees the copy for the next build.</summary>
public sealed class ShadowLease : IDisposable
{
    private readonly Action _release;
    private int _disposed;

    internal ShadowLease(string directory, BuildOutput output, int copiedFiles, Action release)
    {
        Directory = directory;
        Assembly = Path.Combine(directory, Path.GetFileName(output.Assembly));
        DepsFile = Path.Combine(directory, Path.GetFileName(output.DepsFile));
        CopiedFiles = copiedFiles;
        _release = release;
    }

    /// <summary>The copy of the output folder.</summary>
    public string Directory { get; }

    public string Assembly { get; }

    public string DepsFile { get; }

    /// <summary>Files copied for this lease (0 if an up-to-date copy was there).</summary>
    public int CopiedFiles { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _release();
        }
    }
}

/// <summary>
/// Copies of linked projects' build output the model host runs from (ADR 0016): Windows locks the assemblies a process
/// has loaded, so a host running from the real <c>bin</c> folder made every build of the project fail. Per output folder
/// there are a few slots. A slot is reused and brought up to date by copying only the files that changed (size or write
/// time), so after the first start a new build costs only the files it rewrote. A slot in use is never changed: hosts
/// that need the same build share it, a newer build goes into another slot. Other FerretSharp processes (a second
/// instance) are kept out by a lock file per slot, held open while the slot is in use.
/// </summary>
public sealed class BuildOutputShadow
{
    /// <summary>New slots tried beyond the existing ones before giving up (all in use: something is wrong).</summary>
    private const int MaxNewSlots = 8;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _lock = new();
    private readonly Dictionary<string, Slot> _leased = new(StringComparer.OrdinalIgnoreCase);
    private readonly int _attempts;
    private readonly TimeSpan _retryDelay;

    public BuildOutputShadow(string directory)
        : this(directory, attempts: 10, TimeSpan.FromMilliseconds(500))
    {
    }

    /// <param name="attempts">Tries while the build still writes the output (files locked or changing while copied).</param>
    internal BuildOutputShadow(string directory, int attempts, TimeSpan retryDelay)
    {
        Directory = directory;
        _attempts = attempts;
        _retryDelay = retryDelay;
    }

    public string Directory { get; }

    /// <summary>Step reported while the copy is checked and brought up to date.</summary>
    public const string CopyStep = "Kopiere die Build-Ausgabe";

    /// <summary>A copy of exactly the current build output, shared with other hosts that run the same build.</summary>
    /// <exception cref="ClrModelException">The output could not be copied (a build still running after all attempts).</exception>
    public async Task<ShadowLease> AcquireAsync(BuildOutput output, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        progress?.Report(CopyStep);
        var source = Path.GetDirectoryName(Path.GetFullPath(output.Assembly))!;
        var root = Path.Combine(Directory, Key(output, source));
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var failures = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (var attempt = 1; ; attempt++)
            {
                Slot? slot = null;
                try
                {
                    var files = BuildFiles.List(source);
                    if (Shared(root, files) is { } shared)
                    {
                        return Lease(shared, output, 0);
                    }

                    slot = Claim(root, failures.Where(f => f.Value >= 2).Select(f => f.Key).ToHashSet(StringComparer.OrdinalIgnoreCase));
                    var copied = Sync(source, slot.Path, files, cancellationToken);
                    // The build may have gone on meanwhile: the copy must be the output as it is now.
                    if (!BuildFiles.Same(files, BuildFiles.List(source)))
                    {
                        throw new IOException("The build output changed while it was copied.");
                    }

                    slot.Files = files;
                    lock (_lock)
                    {
                        _leased[slot.Path] = slot;
                    }

                    return Lease(slot, output, copied);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    if (slot is not null)
                    {
                        slot.Dispose();
                        failures[slot.Path] = failures.GetValueOrDefault(slot.Path) + 1;
                    }

                    if (attempt >= _attempts)
                    {
                        throw new ClrModelException(ClrModelErrorKind.HostFailed,
                            $"Die Build-Ausgabe ließ sich nicht kopieren – läuft gerade ein Build? {ex.Message}");
                    }

                    await Task.Delay(_retryDelay, cancellationToken);
                }
                catch
                {
                    slot?.Dispose(); // cancelled while copying
                    throw;
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>A slot in use by this process with exactly these files.</summary>
    private Slot? Shared(string root, IReadOnlyDictionary<string, FileStamp> files)
    {
        lock (_lock)
        {
            var slot = _leased.Values.FirstOrDefault(s => s.Root == root && BuildFiles.Same(s.Files, files));
            if (slot is not null)
            {
                slot.Users++;
            }

            return slot;
        }
    }

    /// <summary>
    /// A free slot, the one used last first (closest to the current build, the least to copy); a new one if all are in use
    /// (by this or another FerretSharp process).
    /// </summary>
    /// <param name="avoid">Slots that failed repeatedly in this acquisition (a file still held by a host that was killed).</param>
    private Slot Claim(string root, IReadOnlySet<string> avoid)
    {
        System.IO.Directory.CreateDirectory(root);
        var used = System.IO.Directory.EnumerateFiles(root, "*.lock")
            .Select(f => (Number: int.TryParse(Path.GetFileNameWithoutExtension(f), out var n) ? n : 0, At: File.GetLastWriteTimeUtc(f)))
            .Where(s => s.Number > 0)
            .OrderByDescending(s => s.At)
            .Select(s => s.Number)
            .ToList();
        var fresh = Enumerable.Range(1, used.Count + MaxNewSlots).Where(n => !used.Contains(n));
        foreach (var number in used.Concat(fresh))
        {
            var path = Path.Combine(root, number.ToString(System.Globalization.CultureInfo.InvariantCulture));
            lock (_lock)
            {
                if (_leased.ContainsKey(path) || avoid.Contains(path))
                {
                    continue;
                }
            }

            try
            {
                var lockFile = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                // The write time orders the slots by last use.
                lockFile.SetLength(0);
                lockFile.Write(Encoding.ASCII.GetBytes(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                lockFile.Flush();
                return new Slot(root, path, lockFile);
            }
            catch (IOException)
            {
                // in use by another FerretSharp process
            }
        }

        throw new IOException("No free copy of the build output.");
    }

    /// <summary>Brings the copy up to date: copies new and changed files, deletes those the build no longer has.</summary>
    /// <returns>The number of files copied.</returns>
    private static int Sync(string source, string target, IReadOnlyDictionary<string, FileStamp> files, CancellationToken cancellationToken)
    {
        System.IO.Directory.CreateDirectory(target);
        var existing = BuildFiles.List(target);
        var copied = 0;
        foreach (var (relative, stamp) in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (existing.TryGetValue(relative, out var have) && have == stamp)
            {
                continue;
            }

            var to = Path.Combine(target, relative);
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(Path.Combine(source, relative), to, overwrite: true);
            // The source's stamp, set only once the copy is complete: an interrupted copy is copied again next time.
            File.SetLastWriteTimeUtc(to, new DateTime(stamp.WriteTicks, DateTimeKind.Utc));
            copied++;
        }

        foreach (var relative in existing.Keys.Where(f => !files.ContainsKey(f)))
        {
            File.Delete(Path.Combine(target, relative));
        }

        return copied;
    }

    private ShadowLease Lease(Slot slot, BuildOutput output, int copied) => new(slot.Path, output, copied, () => Release(slot));

    private void Release(Slot slot)
    {
        lock (_lock)
        {
            if (--slot.Users > 0)
            {
                return;
            }

            _leased.Remove(slot.Path);
        }

        slot.Dispose();
    }

    /// <summary>A readable folder name per output folder: <c>Shop.Data-1A2B3C4D5E6F</c>.</summary>
    private static string Key(BuildOutput output, string source)
    {
        var path = OperatingSystem.IsWindows() ? source.ToUpperInvariant() : source;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path)))[..12];
        return Path.GetFileNameWithoutExtension(output.Assembly) + "-" + hash;
    }

    private sealed class Slot(string root, string path, FileStream lockFile) : IDisposable
    {
        public string Root { get; } = root;

        public string Path { get; } = path;

        public IReadOnlyDictionary<string, FileStamp> Files { get; set; } = new Dictionary<string, FileStamp>();

        /// <summary>Leases sharing the slot (under the shadow's lock).</summary>
        public int Users { get; set; } = 1;

        public void Dispose() => lockFile.Dispose();
    }
}
