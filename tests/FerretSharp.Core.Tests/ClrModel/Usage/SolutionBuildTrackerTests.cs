using FerretSharp.Core.ClrModel;

namespace FerretSharp.Core.Tests.ClrModel.Usage;

/// <summary>Progress of "Build projects", counted from the lines of <c>dotnet build -v minimal</c>.</summary>
public sealed class SolutionBuildTrackerTests
{
    private static SolutionBuildTracker Tracker() => new([
        Path.Combine("C:", "src", "Shop.Data", "Shop.Data.csproj"),
        Path.Combine("C:", "src", "Shop.Web", "Shop.Web.csproj"),
        Path.Combine("C:", "src", "Shop Tools", "Shop Tools.fsproj"),
        Path.Combine("C:", "src", "Shared", "Shared.shproj"),
    ]);

    [Fact]
    public void Each_project_of_the_solution_counts_once_when_it_wrote_its_output()
    {
        var tracker = Tracker();
        Assert.Equal(new SolutionBuildProgress(0, 3, null), tracker.Progress); // the shared project never builds
        Assert.Equal(0, tracker.Progress.Fraction);

        Assert.True(tracker.Observe(@"  Shop.Web -> C:\src\Shop.Web\bin\Debug\net8.0\Shop.Web.dll"));
        Assert.Equal(new SolutionBuildProgress(1, 3, "Shop.Web"), tracker.Progress);

        // Another target framework of the same project, other casing: no step.
        Assert.False(tracker.Observe(@"  shop.web -> C:\src\Shop.Web\bin\Debug\net9.0\Shop.Web.dll"));
        Assert.True(tracker.Observe(@"  Shop Tools -> C:\src\Shop Tools\bin\Debug\net8.0\Shop Tools.dll"));
        Assert.True(tracker.Observe(@"  Shop.Data -> C:\src\Shop.Data\bin\Debug\net8.0\Shop.Data.dll"));

        Assert.Equal(new SolutionBuildProgress(3, 3, "Shop.Data"), tracker.Progress);
        Assert.Equal(1, tracker.Progress.Fraction);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  Determining projects to restore...")]
    [InlineData(@"  All projects are up-to-date for restore.")]
    [InlineData(@"C:\src\Shop.Data\Customer.cs(12,5): error CS1002: ; expected [C:\src\Shop.Data\Shop.Data.csproj]")]
    [InlineData(@"C:\src\Shop.Data\Map.cs(3,1): warning CS8618: a -> b [C:\src\Shop.Data\Shop.Data.csproj]")]
    [InlineData(@"  Other.Lib -> C:\elsewhere\Other.Lib.dll")]
    [InlineData(@" -> C:\src\Shop.Data\bin\Shop.Data.dll")]
    public void Lines_that_name_no_project_of_the_solution_count_nothing(string line)
    {
        var tracker = Tracker();

        Assert.False(tracker.Observe(line));
        Assert.Equal(0, tracker.Progress.Done);
    }

    [Fact]
    public void Without_the_solutions_projects_there_is_no_fraction()
    {
        var tracker = new SolutionBuildTracker([]);

        Assert.False(tracker.Observe(@"  Shop.Data -> C:\src\Shop.Data\bin\Shop.Data.dll"));
        Assert.Null(tracker.Progress.Fraction);
    }
}
