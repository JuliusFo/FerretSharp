using FerretSharp.Core.ClrModel;
using FerretSharp.Core.Tests.ClrModel.UsageSamples;
using Microsoft.Extensions.Time.Testing;

namespace FerretSharp.Core.Tests.ClrModel.Usage;

/// <summary>Finding the solution of a linked project, its projects' assemblies, and the cache by MVID.</summary>
public sealed class SolutionAndBuilderTests : IDisposable
{
    private readonly TestFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    private string Project(string relative)
    {
        var file = _folder.Combine(relative.Split('/'));
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>");
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddHours(-2));
        return file;
    }

    /// <summary>Puts a real assembly (this test assembly, with its PDB) where the project's build would be.</summary>
    private static string Built(string project, string assemblyName)
    {
        var output = Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(project)!, "bin", "Debug", "net8.0"));
        var source = typeof(Customer).Assembly.Location;
        var dll = Path.Combine(output.FullName, assemblyName + ".dll");
        File.Copy(source, dll);
        File.Copy(Path.ChangeExtension(source, ".pdb"), Path.ChangeExtension(dll, ".pdb"));
        File.SetLastWriteTimeUtc(dll, DateTime.UtcNow.AddHours(-1));
        return dll;
    }

    [Fact]
    public void Both_solution_formats_list_their_projects_and_the_nearest_solution_with_the_project_wins()
    {
        var data = Project("src/Shop.Data/Shop.Data.csproj");
        var web = Project("src/Shop.Web/Shop.Web.csproj");
        File.WriteAllText(_folder.Combine("Shop.sln"), """
            Microsoft Visual Studio Solution File, Format Version 12.00
            Project("{2150E333-8FDC-42A3-9474-1A3956D46DE8}") = "src", "src", "{11111111-1111-1111-1111-111111111111}"
            EndProject
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Shop.Data", "src\Shop.Data\Shop.Data.csproj", "{22222222-2222-2222-2222-222222222222}"
            EndProject
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Shop.Web", "src\Shop.Web\Shop.Web.csproj", "{33333333-3333-3333-3333-333333333333}"
            EndProject
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Gone", "src\Gone\Gone.csproj", "{44444444-4444-4444-4444-444444444444}"
            EndProject
            """);
        File.WriteAllText(_folder.Combine("Client.slnx"), """<Solution><Folder Name="/src/"><Project Path="src/Shop.Web/Shop.Web.csproj" /></Folder></Solution>""");
        File.WriteAllText(_folder.Combine("Server.slnx"), """<Solution><Folder Name="/src/"><Project Path="src\Shop.Data\Shop.Data.csproj" /></Folder></Solution>""");

        Assert.Equal([data, web], SolutionFile.Projects(_folder.Combine("Shop.sln")));
        Assert.Equal([web], SolutionFile.Projects(_folder.Combine("Client.slnx")));
        Assert.Equal([_folder.Combine("Server.slnx"), _folder.Combine("Shop.sln")], SolutionFile.Find(data));
        Assert.Empty(SolutionFile.Find(Project("other/Other.csproj")));
    }

    [Fact]
    public void A_project_is_built_stale_or_not_built()
    {
        var built = Project("Built/Built.csproj");
        Built(built, "Built");
        var stale = Project("Stale/Stale.csproj");
        Built(stale, "Stale");
        File.WriteAllText(_folder.Combine("Stale", "Changed.cs"), "class C {}");
        var none = Project("None/None.csproj");

        Assert.True(BuildOutputLocator.FindAssembly(built, "Debug") is { IsBuilt: true, IsStale: false });
        Assert.Equal(_folder.Combine("Stale", "Changed.cs"), BuildOutputLocator.FindAssembly(stale, "Debug").NewerSource);
        Assert.False(BuildOutputLocator.FindAssembly(none, "Debug").IsBuilt);
        Assert.False(BuildOutputLocator.FindAssembly(_folder.Combine("Missing", "Missing.csproj"), "Debug").IsBuilt);
    }

    [Fact]
    public async Task The_builder_reads_each_assembly_once_and_takes_it_from_the_cache_by_mvid()
    {
        var project = Project("Shop/Shop.csproj");
        Built(project, "Shop");
        Project("NotBuilt/NotBuilt.csproj");
        File.WriteAllText(_folder.Combine("Shop.slnx"), """<Solution><Project Path="Shop/Shop.csproj" /><Project Path="NotBuilt/NotBuilt.csproj" /></Solution>""");
        var cache = _folder.Combine("cache");
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var builder = new CodeUsageBuilder(cache, time);
        var link = new ClrProjectLink(project);

        var first = await builder.BuildAsync(link, _folder.Combine("Shop.slnx"), CodeUsageIndexTests.Model(), TestContext.Current.CancellationToken);

        Assert.Equal(["Shop", "NotBuilt"], first.Projects.Select(p => p.Name));
        Assert.Null(first.Projects[1].Uses);
        Assert.False(first.Projects[1].Build!.IsBuilt);
        var file = Assert.Single(Directory.GetFiles(cache));
        Assert.Equal(CodeUsageBuilder.CacheFileName("Shop", typeof(Customer).Assembly.ManifestModule.ModuleVersionId, ["Shop", "NotBuilt"]), Path.GetFileName(file));
        Assert.DoesNotContain('\n', File.ReadAllText(file)); // a cache: not indented (review 3.44.0, A11)
        var customer = first.Index.Model.Entities.Single(e => e.Name == "Customer");
        Assert.NotEmpty(first.Index.ForProperty(customer, "Email"));

        // A cached result is used as it is: mark it, build again.
        File.WriteAllText(file, File.ReadAllText(file).Replace("\"ReadEmail\"", "\"FromCache\"", StringComparison.Ordinal));
        var second = await builder.BuildAsync(link, _folder.Combine("Shop.slnx"), CodeUsageIndexTests.Model(), TestContext.Current.CancellationToken);
        Assert.Contains(second.Index.ForProperty(customer, "Email"), u => u.Use.CallerMethod == "FromCache");

        // Another set of solution assemblies, or a broken file: read again.
        File.WriteAllText(file, "{ broken");
        var third = await builder.BuildAsync(link, null, CodeUsageIndexTests.Model(), TestContext.Current.CancellationToken);
        Assert.Contains(third.Index.ForProperty(customer, "Email"), u => u.Use.CallerMethod == "ReadEmail");
        Assert.Null(third.Solution);
    }

    [Fact]
    public async Task Cache_files_unused_for_thirty_days_are_removed()
    {
        var cache = Directory.CreateDirectory(_folder.Combine("cache")).FullName;
        var old = Path.Combine(cache, "old.json");
        var recent = Path.Combine(cache, "recent.json");
        File.WriteAllText(old, "{}");
        File.WriteAllText(recent, "{}");
        var now = DateTimeOffset.UtcNow;
        File.SetLastWriteTimeUtc(old, now.AddDays(-31).UtcDateTime);
        File.SetLastWriteTimeUtc(recent, now.AddDays(-29).UtcDateTime);

        await new CodeUsageBuilder(cache, new FakeTimeProvider(now)).BuildAsync(new ClrProjectLink(Project("P/P.csproj")), null, CodeUsageIndexTests.Model(), TestContext.Current.CancellationToken);

        Assert.False(File.Exists(old));
        Assert.True(File.Exists(recent));
    }

    /// <summary>
    /// Review 3.44.0, A11: every build of a changed project left a new file for 30 days. Of one assembly the most recently
    /// used few stay; files of other assemblies (and of another form) are not counted against them.
    /// </summary>
    [Fact]
    public async Task Of_one_assembly_only_the_most_recently_used_builds_stay()
    {
        var cache = Directory.CreateDirectory(_folder.Combine("cache")).FullName;
        var now = DateTimeOffset.UtcNow;
        var builds = Enumerable.Range(0, CodeUsageBuilder.BuildsKeptPerAssembly + 2)
            .Select(i => (File: Path.Combine(cache, CodeUsageBuilder.CacheFileName("Shop.Data", Guid.NewGuid(), ["Shop.Data"])), Used: now.AddHours(-i)))
            .ToList();
        var other = Path.Combine(cache, CodeUsageBuilder.CacheFileName("Shop.Web", Guid.NewGuid(), ["Shop.Web"]));
        foreach (var (file, used) in builds.Append((other, now.AddDays(-1))))
        {
            File.WriteAllText(file, "{}");
            File.SetLastWriteTimeUtc(file, used.UtcDateTime);
        }

        await new CodeUsageBuilder(cache, new FakeTimeProvider(now)).BuildAsync(new ClrProjectLink(Project("P/P.csproj")), null, CodeUsageIndexTests.Model(), TestContext.Current.CancellationToken);

        Assert.Equal(builds.Take(CodeUsageBuilder.BuildsKeptPerAssembly).Select(b => b.File).Append(other).Order(), Directory.GetFiles(cache).Order());
    }

    /// <summary>Review 3.44.0, A27: a result holds only the uses of the solution's types – another solution, another file.</summary>
    [Fact]
    public void The_cache_file_names_the_set_of_solution_assemblies()
    {
        var mvid = Guid.NewGuid();

        Assert.Equal(CodeUsageBuilder.CacheFileName("Shop", mvid, ["Shop", "Shop.Data"]), CodeUsageBuilder.CacheFileName("Shop", mvid, ["shop.data", "SHOP"]));
        Assert.NotEqual(CodeUsageBuilder.CacheFileName("Shop", mvid, ["Shop"]), CodeUsageBuilder.CacheFileName("Shop", mvid, ["Shop", "Shop.Data"]));
    }
}
