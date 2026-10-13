using FerretSharp.Core;
using FerretSharp.Core.IO;
using FerretSharp.Testing;

namespace FerretSharp.Core.Tests.IO;

public class InstanceLockTests
{
    [Fact]
    public void A_second_start_on_the_same_data_folder_gets_the_first_ones_process_id()
    {
        using var folder = new TestFolder();
        var paths = new AppPaths(folder.Combine("data"));

        Assert.True(InstanceLock.TryAcquire(paths, 4711, out var first, out _));
        using (first)
        {
            Assert.False(InstanceLock.TryAcquire(paths, 815, out var second, out var other));
            Assert.Null(second);
            Assert.Equal(4711, other);
        }
    }

    [Fact]
    public void The_lock_is_free_again_once_the_first_instance_lets_go()
    {
        using var folder = new TestFolder();
        var paths = new AppPaths(folder.Path);

        Assert.True(InstanceLock.TryAcquire(paths, 4711, out var first, out _));
        first.Dispose();

        Assert.True(InstanceLock.TryAcquire(paths, 815, out var second, out var other));
        second.Dispose();
        Assert.Null(other);
    }

    [Fact]
    public void Another_data_folder_is_another_lock()
    {
        using var folder = new TestFolder();

        Assert.True(InstanceLock.TryAcquire(new AppPaths(folder.Combine("a")), 1, out var a, out _));
        Assert.True(InstanceLock.TryAcquire(new AppPaths(folder.Combine("b")), 2, out var b, out _));
        a.Dispose();
        b.Dispose();
    }

    [Fact]
    public void An_unreadable_process_file_still_reports_the_running_instance()
    {
        using var folder = new TestFolder();
        var paths = new AppPaths(folder.Path);

        Assert.True(InstanceLock.TryAcquire(paths, 4711, out var first, out _));
        using (first)
        {
            File.WriteAllText(paths.InstanceProcessFile, "not a number");

            Assert.False(InstanceLock.TryAcquire(paths, 815, out _, out var other));
            Assert.Null(other);
        }
    }
}
