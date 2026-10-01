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

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Rejects_blank_root(string root)
    {
        Assert.Throws<ArgumentException>(() => new AppPaths(root));
    }
}
