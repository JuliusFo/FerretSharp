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

    public void Dispose() => _folder.Dispose();
}
