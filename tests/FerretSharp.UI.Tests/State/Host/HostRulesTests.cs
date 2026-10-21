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
        const string file = @"C:\repo\src\Shop.Data\Customer.cs";

        Assert.Equal(int.MaxValue, VisualStudioMatch.Score(@"C:\repo\Shop.slnx", file, @"C:\REPO\Shop.slnx"));
        Assert.True(VisualStudioMatch.Score(@"C:\repo\src\Shop.slnx", file, null) > VisualStudioMatch.Score(@"C:\repo\All.slnx", file, null));
        Assert.Equal(-1, VisualStudioMatch.Score(@"C:\other\Other.slnx", file, null));
        Assert.Equal(-1, VisualStudioMatch.Score(@"C:\re\Re.slnx", @"C:\repo\x.cs", null)); // C:\re is no folder of C:\repo
    }
}
