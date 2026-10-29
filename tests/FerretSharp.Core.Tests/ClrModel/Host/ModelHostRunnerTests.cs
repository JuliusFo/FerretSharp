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

    public void Dispose() => _folder.Dispose();
}
