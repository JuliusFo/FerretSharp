using FerretSharp.Core.ClrModel;

namespace FerretSharp.Core.Tests.ClrModel;

/// <summary>The watcher on the build output (ADR 0016) reports a build once, after it has finished writing.</summary>
public sealed class BuildOutputWatcherTests : IDisposable
{
    private static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(300);

    private readonly TestFolder _folder = new();
    private int _reports;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _folder.Dispose();

    private BuildOutputWatcher Watch(bool depsOnly = false) =>
        new(_folder.Path, depsOnly, Quiet, () => Interlocked.Increment(ref _reports));

    /// <summary>Waits until the watcher has reported (or a generous time has passed), then a little longer for extra reports.</summary>
    private async Task<int> ReportsAsync()
    {
        for (var waited = 0; waited < 5000 && Volatile.Read(ref _reports) == 0; waited += 50)
        {
            await Task.Delay(50, Ct);
        }

        await Task.Delay(Quiet * 2, Ct);
        return Volatile.Read(ref _reports);
    }

    [Fact]
    public async Task A_build_writing_many_files_is_reported_once_after_it_finished()
    {
        using var watcher = Watch();

        foreach (var name in new[] { "Shop.Entities.dll", "Shop.Data.dll", "Shop.Data.pdb", "Shop.Data.deps.json" })
        {
            await File.WriteAllTextAsync(Path.Combine(_folder.Path, name), name, Ct);
            await Task.Delay(50, Ct);
        }

        Assert.Equal(1, await ReportsAsync());
    }

    [Fact]
    public async Task Without_an_output_only_a_deps_json_counts()
    {
        using var watcher = Watch(depsOnly: true);

        var obj = Directory.CreateDirectory(Path.Combine(_folder.Path, "obj"));
        await File.WriteAllTextAsync(Path.Combine(obj.FullName, "Shop.Data.AssemblyInfo.cs"), "// design-time build", Ct);
        await Task.Delay(Quiet * 3, Ct);
        Assert.Equal(0, Volatile.Read(ref _reports));

        var bin = Directory.CreateDirectory(Path.Combine(_folder.Path, "bin", "Debug", "net8.0"));
        await File.WriteAllTextAsync(Path.Combine(bin.FullName, "Shop.Data.deps.json"), "{}", Ct);
        Assert.Equal(1, await ReportsAsync());
    }

    [Fact]
    public async Task Nothing_is_reported_after_dispose()
    {
        var watcher = Watch();
        await File.WriteAllTextAsync(Path.Combine(_folder.Path, "Shop.Data.dll"), "x", Ct);
        watcher.Dispose();

        await Task.Delay(Quiet * 3, Ct);

        Assert.Equal(0, Volatile.Read(ref _reports));
    }
}
