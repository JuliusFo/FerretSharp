using FerretSharp.Core.ClrModel;

namespace FerretSharp.Core.Tests.ClrModel;

/// <summary>
/// The copies of the build output the model host runs from (ADR 0016): the real output is never held open, a new build
/// costs only the files it changed, and a copy a host runs from is never changed under it.
/// </summary>
public sealed class BuildOutputShadowTests : IDisposable
{
    private readonly TestFolder _folder = new();
    private readonly string _output;
    private readonly BuildOutput _build;
    private readonly BuildOutputShadow _shadow;

    public BuildOutputShadowTests()
    {
        _output = Directory.CreateDirectory(_folder.Combine("Shop.Data", "bin", "Debug", "net8.0")).FullName;
        Write("Shop.Data.dll", "data v1");
        Write("Shop.Entities.dll", "entities v1");
        Write("Shop.Data.deps.json", "{}");
        Write(Path.Combine("de", "Shop.Data.resources.dll"), "de v1");
        _build = new BuildOutput(Path.Combine(_output, "Shop.Data.dll"), Path.Combine(_output, "Shop.Data.deps.json"), new Version(8, 0),
            DateTime.UtcNow, [], [], null);
        _shadow = new BuildOutputShadow(_folder.Combine("shadow"), attempts: 2, TimeSpan.FromMilliseconds(10));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _folder.Dispose();

    /// <summary>Writes a file of the build output with a distinct write time (as a build does).</summary>
    private void Write(string relative, string content, int minutes = 0)
    {
        var path = Path.Combine(_output, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        File.SetLastWriteTimeUtc(path, new DateTime(2026, 10, 8, 10, minutes, 0, DateTimeKind.Utc));
    }

    private static string Read(ShadowLease lease, string relative) => File.ReadAllText(Path.Combine(lease.Directory, relative));

    [Fact]
    public async Task The_host_gets_a_complete_copy_and_the_output_stays_writable()
    {
        using var lease = await _shadow.AcquireAsync(_build, null, Ct);

        Assert.Equal(4, lease.CopiedFiles);
        Assert.Equal(Path.Combine(lease.Directory, "Shop.Data.dll"), lease.Assembly);
        Assert.Equal(Path.Combine(lease.Directory, "Shop.Data.deps.json"), lease.DepsFile);
        Assert.Equal("de v1", Read(lease, Path.Combine("de", "Shop.Data.resources.dll")));
        Assert.DoesNotContain(_output, lease.Directory, StringComparison.OrdinalIgnoreCase);
        using (new FileStream(_build.Assembly, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            // a build can overwrite the DLL while the copy is in use
        }
    }

    [Fact]
    public async Task Hosts_on_the_same_build_share_one_copy()
    {
        var progress = new List<string>();
        using var first = await _shadow.AcquireAsync(_build, new Progress(progress), Ct);
        using var second = await _shadow.AcquireAsync(_build, new Progress(progress), Ct);

        Assert.Equal(first.Directory, second.Directory);
        Assert.Equal(0, second.CopiedFiles);
        Assert.Equal([BuildOutputShadow.CopyStep, BuildOutputShadow.CopyStep], progress);
    }

    [Fact]
    public async Task A_free_copy_is_brought_up_to_date_with_only_the_changed_files()
    {
        string directory;
        using (var lease = await _shadow.AcquireAsync(_build, null, Ct))
        {
            directory = lease.Directory;
        }

        Write("Shop.Entities.dll", "entities v2", minutes: 5);
        File.Delete(Path.Combine(_output, "de", "Shop.Data.resources.dll"));
        using var next = await _shadow.AcquireAsync(_build, null, Ct);

        Assert.Equal(directory, next.Directory);
        Assert.Equal(1, next.CopiedFiles);
        Assert.Equal("entities v2", Read(next, "Shop.Entities.dll"));
        Assert.False(File.Exists(Path.Combine(next.Directory, "de", "Shop.Data.resources.dll")));
    }

    [Fact]
    public async Task A_copy_in_use_is_never_changed_a_newer_build_goes_into_another()
    {
        using var running = await _shadow.AcquireAsync(_build, null, Ct);
        Write("Shop.Data.dll", "data v2", minutes: 5);

        using var next = await _shadow.AcquireAsync(_build, null, Ct);

        Assert.NotEqual(running.Directory, next.Directory);
        Assert.Equal("data v1", Read(running, "Shop.Data.dll"));
        Assert.Equal("data v2", Read(next, "Shop.Data.dll"));
    }

    [Fact]
    public async Task A_copy_locked_by_another_ferretsharp_is_left_alone()
    {
        string directory;
        using (var lease = await _shadow.AcquireAsync(_build, null, Ct))
        {
            directory = lease.Directory;
        }

        // Another FerretSharp process holds the slot's lock file while its host runs from the copy.
        using (new FileStream(directory + ".lock", FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            using var lease = await _shadow.AcquireAsync(_build, null, Ct);

            Assert.NotEqual(directory, lease.Directory);
        }
    }

    [Fact]
    public async Task A_file_the_build_still_writes_is_reported_after_the_attempts()
    {
        using var writing = new FileStream(_build.Assembly, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var error = await Assert.ThrowsAsync<ClrModelException>(() => _shadow.AcquireAsync(_build, null, Ct));

        Assert.Contains("Build", error.Message);
    }

    [Fact]
    public async Task A_copy_interrupted_by_a_running_build_is_completed_the_next_time()
    {
        using (new FileStream(Path.Combine(_output, "Shop.Entities.dll"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            await Assert.ThrowsAsync<ClrModelException>(() => _shadow.AcquireAsync(_build, null, Ct));
        }

        using var lease = await _shadow.AcquireAsync(_build, null, Ct);

        Assert.Equal("entities v1", Read(lease, "Shop.Entities.dll"));
        Assert.True(BuildFiles.Same(BuildFiles.List(_output), BuildFiles.List(lease.Directory)));
    }

    /// <summary>A released copy whose last use (the write time of its lock file) lies this far back.</summary>
    private async Task<string> UnusedCopyAsync(TimeSpan ago)
    {
        string directory;
        using (var lease = await _shadow.AcquireAsync(_build, null, Ct))
        {
            directory = lease.Directory;
        }

        File.SetLastWriteTimeUtc(directory + ".lock", DateTime.UtcNow - ago);
        return directory;
    }

    [Fact]
    public async Task Copies_unused_for_a_week_are_removed_with_their_lock_and_empty_output_folder()
    {
        var directory = await UnusedCopyAsync(TimeSpan.FromDays(8));
        var root = Path.GetDirectoryName(directory)!;

        Assert.Equal(1, await _shadow.DeleteUnusedAsync(BuildOutputShadow.UnusedAfter, Ct));

        Assert.False(Directory.Exists(directory));
        Assert.False(File.Exists(directory + ".lock"));
        Assert.False(Directory.Exists(root));
        Assert.True(File.Exists(_build.Assembly)); // the build output itself is never touched
    }

    [Fact]
    public async Task Recently_used_copies_stay()
    {
        var directory = await UnusedCopyAsync(TimeSpan.FromDays(2));

        Assert.Equal(0, await _shadow.DeleteUnusedAsync(BuildOutputShadow.UnusedAfter, Ct));

        Assert.True(File.Exists(Path.Combine(directory, "Shop.Data.dll")));
    }

    [Fact]
    public async Task A_copy_in_use_stays_however_old()
    {
        using var lease = await _shadow.AcquireAsync(_build, null, Ct);
        File.SetLastWriteTimeUtc(lease.Directory + ".lock", DateTime.UtcNow.AddDays(-30));
        var other = await UnusedCopyAsync(TimeSpan.FromDays(30)); // the same build: shares the copy in use

        Assert.Equal(lease.Directory, other);
        Assert.Equal(0, await _shadow.DeleteUnusedAsync(BuildOutputShadow.UnusedAfter, Ct));
        Assert.True(File.Exists(lease.Assembly));
    }

    [Fact]
    public async Task A_copy_another_ferretsharp_uses_stays()
    {
        var directory = await UnusedCopyAsync(TimeSpan.FromDays(30));
        var lockFile = directory + ".lock";
        var lastUse = File.GetLastWriteTimeUtc(lockFile);

        using (new FileStream(lockFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Equal(0, await _shadow.DeleteUnusedAsync(BuildOutputShadow.UnusedAfter, Ct));
        }

        Assert.True(File.Exists(Path.Combine(directory, "Shop.Data.dll")));
        Assert.Equal(lastUse, File.GetLastWriteTimeUtc(lockFile));
    }

    [Fact]
    public async Task Nothing_to_remove_without_any_copies()
    {
        Assert.Equal(0, await _shadow.DeleteUnusedAsync(BuildOutputShadow.UnusedAfter, Ct));
    }

    /// <summary>Synchronous, unlike <see cref="Progress{T}"/>.</summary>
    private sealed class Progress(List<string> steps) : IProgress<string>
    {
        public void Report(string value) => steps.Add(value);
    }
}
