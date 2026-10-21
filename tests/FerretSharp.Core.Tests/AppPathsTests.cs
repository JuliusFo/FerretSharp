using FerretSharp.Core;

namespace FerretSharp.Core.Tests;

public class AppPathsTests
{
    [Fact]
    public void Derived_paths_live_below_root()
    {
        var root = Path.Combine(Path.GetTempPath(), "ferret-test");
        var paths = new AppPaths(root);

        Assert.StartsWith(root, paths.LogsDirectory);
        Assert.StartsWith(root, paths.ConnectionsFile);
        Assert.StartsWith(root, paths.WorkspacesDirectory);
    }

    [Fact]
    public void Default_root_ends_with_app_folder()
    {
        Assert.Equal(AppPaths.AppFolderName, Path.GetFileName(AppPaths.Default.Root));
    }

    /// <summary>
    /// Review 3.44.0, A11: the caches went into the roaming folder, carried along at every logon. They live in the local
    /// one now; a data folder of its own (<c>--data-dir</c>) keeps them below it, and the former folder is removed.
    /// </summary>
    [Fact]
    public void Caches_live_in_the_local_folder_or_below_a_data_folder_of_its_own()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        Assert.StartsWith(local, AppPaths.Default.ModelCacheDirectory, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(local, AppPaths.Default.CodeUsageCacheDirectory, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(Path.Combine(AppPaths.Default.Root, "cache"), AppPaths.Default.FormerCacheDirectory);

        var root = Path.Combine(Path.GetTempPath(), "ferret-test");
        var own = new AppPaths(root);
        Assert.Equal(Path.Combine(root, "cache", "models"), own.ModelCacheDirectory);
        Assert.Null(own.FormerCacheDirectory);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Rejects_blank_root(string root)
    {
        Assert.Throws<ArgumentException>(() => new AppPaths(root));
    }
}
