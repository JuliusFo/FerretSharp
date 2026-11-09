using FerretSharp.Core;
using FerretSharp.Core.ClrModel;

namespace FerretSharp.Integration.Tests;

/// <summary>
/// "Build projects" with a real <c>dotnet build</c>: its progress is counted from the output of minimal verbosity, which a
/// quiet build would not give. Needs only the SDK the tests run with, no database and no packages.
/// </summary>
public sealed class SolutionBuildTests : IDisposable
{
    private readonly TestFolder _folder = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Every_project_of_the_solution_is_counted_once_as_it_builds()
    {
        // One name with a space; several target frameworks and shared projects: SolutionBuildTrackerTests (no packages here).
        var framework = $"<TargetFramework>net{Environment.Version.Major}.0</TargetFramework>";
        Project("Shop.Data", framework);
        Project("Shop Web", framework);
        var solution = _folder.Combine("Shop.slnx");
        File.WriteAllText(solution, """
            <Solution>
              <Project Path="Shop.Data/Shop.Data.csproj" />
              <Project Path="Shop Web/Shop Web.csproj" />
            </Solution>
            """);
        var steps = new List<SolutionBuildProgress>();
        var progress = new SyncProgress<SolutionBuildProgress>(step =>
        {
            lock (steps)
            {
                steps.Add(step);
            }
        });

        var run = await new ModelHostRunner("unused.dll").BuildSolutionAsync(solution, "Debug", progress, Ct);

        Assert.True(run.Succeeded, run.Output);
        lock (steps)
        {
            Assert.Equal(new SolutionBuildProgress(0, 2, null), steps[0]);
            Assert.Equal([1, 2], steps.Skip(1).Select(s => s.Done));
            Assert.Equal(["Shop Web", "Shop.Data"], steps.Skip(1).Select(s => s.Last!).Order(StringComparer.Ordinal));
        }
    }

    private void Project(string name, string framework)
    {
        Directory.CreateDirectory(_folder.Combine(name));
        File.WriteAllText(_folder.Combine(name, name + ".csproj"),
            $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>{framework}</PropertyGroup></Project>");
    }

    public void Dispose() => _folder.Dispose();
}
