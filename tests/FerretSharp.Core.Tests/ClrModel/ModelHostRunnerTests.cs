using FerretSharp.Core.ClrModel;

namespace FerretSharp.Core.Tests.ClrModel;

public sealed class ModelHostRunnerTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("ferret-modelhost-");

    /// <summary>
    /// Work folders that a killed host kept from being deleted are removed on a later start – only old ones, a young one
    /// may belong to another FerretSharp running right now.
    /// </summary>
    [Fact]
    public void Stale_work_folders_are_removed_young_ones_kept()
    {
        var old = _root.CreateSubdirectory("old");
        File.WriteAllText(Path.Combine(old.FullName, "modelhost.runtimeconfig.json"), "{}");
        old.LastWriteTimeUtc = DateTime.UtcNow.AddDays(-2);
        var young = _root.CreateSubdirectory("young");

        ModelHostRunner.DeleteStaleWorkFolders(_root.FullName, TimeSpan.FromDays(1));

        Assert.False(Directory.Exists(old.FullName));
        Assert.True(Directory.Exists(young.FullName));
    }

    [Fact]
    public void A_missing_root_is_nothing_to_clean() =>
        ModelHostRunner.DeleteStaleWorkFolders(Path.Combine(_root.FullName, "missing"), TimeSpan.FromDays(1));

    public void Dispose() => _root.Delete(recursive: true);
}
