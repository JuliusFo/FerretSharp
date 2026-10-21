using FerretSharp.UI.State;

namespace FerretSharp.UI.Tests.State;

/// <summary>Rules of the app's services, out of the app project so they have tests (review 3.44.0, C8).</summary>
public sealed class HostRulesTests
{
    /// <summary>Review 3.40.0, A27: a pipe name taken for good tried every 2 s.</summary>
    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 4)]
    [InlineData(5, 32)]
    [InlineData(8, 256)]
    [InlineData(9, 300)] // 512 s: at most five minutes
    [InlineData(1000, 300)]
    public void The_link_pipe_waits_longer_after_each_failure(int failures, int seconds) =>
        Assert.Equal(TimeSpan.FromSeconds(seconds), LinkPipeRetry.Delay(failures));

    [Fact]
    public void The_solution_itself_wins_then_the_deepest_folder_holding_the_file()
    {
        var file = At("repo", "src", "Shop.Data", "Customer.cs");

        Assert.Equal(int.MaxValue, VisualStudioMatch.Score(At("repo", "Shop.slnx"), file, At("REPO", "Shop.slnx")));
        Assert.True(VisualStudioMatch.Score(At("repo", "src", "Shop.slnx"), file, null) > VisualStudioMatch.Score(At("repo", "All.slnx"), file, null));
        Assert.Equal(-1, VisualStudioMatch.Score(At("other", "Other.slnx"), file, null));
        Assert.Equal(-1, VisualStudioMatch.Score(At("re", "Re.slnx"), At("repo", "x.cs"), null)); // re is no folder of repo
    }

    /// <summary>An absolute path on this platform (C:\… on Windows, /… elsewhere – the CI runs the UI tests on Linux).</summary>
    private static string At(params string[] parts) => Path.Combine([OperatingSystem.IsWindows() ? @"C:\" : "/", .. parts]);
}
