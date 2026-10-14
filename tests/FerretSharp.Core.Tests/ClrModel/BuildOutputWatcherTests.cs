using FerretSharp.Core.ClrModel;
using Microsoft.Extensions.Time.Testing;

namespace FerretSharp.Core.Tests.ClrModel;

/// <summary>
/// The watcher on the build output (ADR 0016) reports a build once, after it has finished writing. The file events are
/// real; the quiet period runs on a fake clock (R5b), so a slow machine cannot end it between two files.
/// </summary>
public sealed class BuildOutputWatcherTests : IDisposable
{
    private static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(300);

    private readonly TestFolder _folder = new();
    private readonly FakeTimeProvider _time = new();
    private int _reports;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _folder.Dispose();

    private int Reports => Volatile.Read(ref _reports);

    private BuildOutputWatcher Watch(bool depsOnly = false) =>
        new(_folder.Path, depsOnly, Quiet, () => Interlocked.Increment(ref _reports), _time);

    /// <summary>Writes the file and waits until the watcher has handled an event for it (counted or not).</summary>
    private static async Task WriteAsync(BuildOutputWatcher watcher, string path)
    {
        var handled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnHandled(string file)
        {
            if (string.Equals(file, path, StringComparison.OrdinalIgnoreCase))
            {
                handled.TrySetResult();
            }
        }

        watcher.Handled += OnHandled;
        try
        {
            await File.WriteAllTextAsync(path, Path.GetFileName(path), Ct);
            await handled.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        }
        finally
        {
            watcher.Handled -= OnHandled;
        }
    }

    /// <summary>
    /// Moves the clock on until the watcher reported: a later event of the same write may move the end of the quiet period
    /// while the clock moves on (as a slow write would with the system clock).
    /// </summary>
    private async Task ReportedAsync()
    {
        for (var waited = 0; Reports == 0; waited += 20)
        {
            Assert.True(waited < 10_000, "The watcher did not report.");
            _time.Advance(Quiet);
            await Task.Delay(20, Ct);
        }
    }

    /// <summary>Waits until no file event came for a while: what one write reports has arrived.</summary>
    private static async Task SettledAsync(BuildOutputWatcher watcher)
    {
        for (var (seen, still) = (watcher.Seen, 0); still < 5; await Task.Delay(20, Ct))
        {
            (seen, still) = watcher.Seen == seen ? (seen, still + 1) : (watcher.Seen, 0);
        }
    }

    [Fact]
    public async Task A_build_writing_many_files_is_reported_once_after_it_finished()
    {
        using var watcher = Watch();

        foreach (var name in new[] { "Shop.Entities.dll", "Shop.Data.dll", "Shop.Data.pdb", "Shop.Data.deps.json" })
        {
            await WriteAsync(watcher, Path.Combine(_folder.Path, name));
            _time.Advance(Quiet / 2); // the next file comes before the quiet period ends
        }

        Assert.Equal(0, Reports);
        await SettledAsync(watcher);
        await ReportedAsync();
        _time.Advance(Quiet * 3);
        Assert.Equal(1, Reports);
    }

    [Fact]
    public async Task Without_an_output_only_a_deps_json_counts()
    {
        using var watcher = Watch(depsOnly: true);

        var obj = Directory.CreateDirectory(Path.Combine(_folder.Path, "obj"));
        await WriteAsync(watcher, Path.Combine(obj.FullName, "Shop.Data.AssemblyInfo.cs"));
        _time.Advance(Quiet * 3);
        Assert.Equal(0, Reports);

        var bin = Directory.CreateDirectory(Path.Combine(_folder.Path, "bin", "Debug", "net8.0"));
        await WriteAsync(watcher, Path.Combine(bin.FullName, "Shop.Data.deps.json"));
        await ReportedAsync();
        Assert.Equal(1, Reports);
    }

    [Fact]
    public async Task Nothing_is_reported_after_dispose()
    {
        var watcher = Watch();
        await WriteAsync(watcher, Path.Combine(_folder.Path, "Shop.Data.dll"));
        watcher.Dispose();

        _time.Advance(Quiet * 3);

        Assert.Equal(0, Reports);
    }
}
