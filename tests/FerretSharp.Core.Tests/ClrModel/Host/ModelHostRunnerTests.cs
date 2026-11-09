using FerretSharp.Core.ClrModel;

namespace FerretSharp.Core.Tests.ClrModel;

public sealed class ModelHostRunnerTests : IDisposable
{
    private readonly TestFolder _folder = new();

    /// <summary>
    /// Work folders that a killed host kept from being deleted are removed on a later start – only old ones, a young one
    /// may belong to another FerretSharp running right now.
    /// </summary>
    [Fact]
    public void Stale_work_folders_are_removed_young_ones_kept()
    {
        var old = Directory.CreateDirectory(_folder.Combine("old"));
        File.WriteAllText(Path.Combine(old.FullName, "modelhost.runtimeconfig.json"), "{}");
        old.LastWriteTimeUtc = DateTime.UtcNow.AddDays(-2);
        var young = Directory.CreateDirectory(_folder.Combine("young"));

        ModelHostRunner.DeleteStaleWorkFolders(_folder.Path, TimeSpan.FromDays(1));

        Assert.False(Directory.Exists(old.FullName));
        Assert.True(Directory.Exists(young.FullName));
    }

    [Fact]
    public void A_missing_root_is_nothing_to_clean() =>
        ModelHostRunner.DeleteStaleWorkFolders(_folder.Combine("missing"), TimeSpan.FromDays(1));

    /// <summary>
    /// Review 3.54.0, A6: two connections linked to one project built it side by side into the same obj/bin. A second build
    /// waits for the first (decision of the user); cancelled while waiting, it never starts.
    /// </summary>
    [Fact]
    public async Task A_second_build_waits_for_the_first_and_a_cancelled_one_never_starts()
    {
        var first = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = ModelHostRunner.OneBuildAtATimeAsync(() => first.Task, TestContext.Current.CancellationToken);
        var secondStarted = false;
        var second = ModelHostRunner.OneBuildAtATimeAsync(() => { secondStarted = true; return Task.FromResult(2); }, TestContext.Current.CancellationToken);
        using var cancel = new CancellationTokenSource();
        var third = ModelHostRunner.OneBuildAtATimeAsync<int>(() => throw new InvalidOperationException("started"), cancel.Token);

        Assert.False(secondStarted);
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => third);
        first.SetResult(1);

        Assert.Equal((1, 2), (await running, await second));
    }

    /// <summary>
    /// Review of the PR: with MSBUILDTERMINALLOGGER=on in the user's environment, dotnet build wrote coloured live lines
    /// instead of "Name -> path", and the progress of "Build projects" stood at 0.
    /// </summary>
    [Fact]
    public void Dotnet_runs_without_the_terminal_logger_whatever_the_environment_says()
    {
        var start = DotNetCli.StartInfo(["build"], _folder.Path);

        Assert.Equal("off", start.Environment["MSBUILDTERMINALLOGGER"]);
    }

    /// <summary>Review of the PR: "0 of 40 projects" stood there while the build still waited for another one.</summary>
    [Fact]
    public async Task A_build_of_the_solution_waiting_for_another_reports_no_progress()
    {
        var first = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = ModelHostRunner.OneBuildAtATimeAsync(() => first.Task, TestContext.Current.CancellationToken);
        var solution = _folder.Combine("Shop.slnx");
        File.WriteAllText(solution, "<Solution />");
        var reports = 0;
        using var cancel = new CancellationTokenSource();

        var waiting = new ModelHostRunner("unused.dll").BuildSolutionAsync(solution, "Debug",
            new SyncProgress<SolutionBuildProgress>(_ => Interlocked.Increment(ref reports)), cancel.Token);
        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.Equal(0, reports);
        first.SetResult(1);
        await running;
    }

    public void Dispose() => _folder.Dispose();
}
