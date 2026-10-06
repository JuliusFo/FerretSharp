using FerretSharp.Core.ClrModel;

namespace FerretSharp.Core.Tests.ClrModel;

/// <summary>Finding the build output of a linked project in a fake project folder (no build involved).</summary>
public sealed class BuildOutputLocatorTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "fs-locator-" + Guid.NewGuid().ToString("N")));

    public void Dispose() => _root.Delete(recursive: true);

    private string Project(string name, string body = "", string sdk = "Microsoft.NET.Sdk")
    {
        var directory = Directory.CreateDirectory(Path.Combine(_root.FullName, name));
        var file = Path.Combine(directory.FullName, name + ".csproj");
        File.WriteAllText(file, $"<Project Sdk=\"{sdk}\"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>{body}</Project>");
        File.WriteAllText(Path.Combine(directory.FullName, "Context.cs"), "class C {}");
        File.SetLastWriteTimeUtc(Path.Combine(directory.FullName, "Context.cs"), DateTime.UtcNow.AddHours(-1));
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddHours(-1));
        return file;
    }

    private static string Built(string project, string assembly, string framework = ".NETCoreApp,Version=v8.0", string configuration = "Debug", string tfm = "net8.0")
    {
        var output = Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(project)!, "bin", configuration, tfm));
        File.WriteAllText(Path.Combine(output.FullName, assembly + ".deps.json"), $$"""{ "runtimeTarget": { "name": "{{framework}}" } }""");
        var dll = Path.Combine(output.FullName, assembly + ".dll");
        File.WriteAllText(dll, "");
        return dll;
    }

    [Fact]
    public void Finds_the_output_of_the_configuration_with_framework_and_package_folders()
    {
        var project = Project("Shop.Data");
        var dll = Built(project, "Shop.Data");
        Built(project, "Shop.Data", configuration: "Release");
        var packages = Directory.CreateDirectory(Path.Combine(_root.FullName, "packages"));
        Directory.CreateDirectory(Path.Combine(_root.FullName, "Shop.Data", "obj"));
        File.WriteAllText(Path.Combine(_root.FullName, "Shop.Data", "obj", "project.assets.json"),
            // NuGet writes the folder with a trailing separator – '\' on Windows, '/' elsewhere (CI runs on Linux too).
            $$"""{ "packageFolders": { {{System.Text.Json.JsonSerializer.Serialize(packages.FullName + Path.DirectorySeparatorChar)}}: {} } }""");

        var output = BuildOutputLocator.Find(new ClrProjectLink(project));

        Assert.Equal(dll, output.Assembly);
        Assert.Equal(new Version(8, 0), output.TargetFramework);
        Assert.Equal([packages.FullName + Path.DirectorySeparatorChar], output.PackageFolders);
        Assert.False(output.IsStale);
    }

    [Fact]
    public void A_source_file_newer_than_the_build_also_in_a_referenced_project_makes_it_stale()
    {
        var entities = Project("Shop.Entities");
        var project = Project("Shop.Data", """<ItemGroup><ProjectReference Include="..\Shop.Entities\Shop.Entities.csproj" /></ItemGroup>""");
        var dll = Built(project, "Shop.Data");
        File.SetLastWriteTimeUtc(dll, DateTime.UtcNow.AddMinutes(-10));
        var changed = Path.Combine(Path.GetDirectoryName(entities)!, "Kunde.cs");
        File.WriteAllText(changed, "class Kunde {}");
        // Files in bin and obj never count.
        Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(project)!, "obj"));
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(project)!, "obj", "Generated.cs"), "");

        var output = BuildOutputLocator.Find(new ClrProjectLink(project));

        Assert.True(output.IsStale);
        Assert.Equal(changed, output.NewerSource);
    }

    [Fact]
    public void Assembly_name_and_web_sdk_come_from_the_project_file()
    {
        var project = Project("Shop.Api", "<PropertyGroup><AssemblyName>Shop</AssemblyName></PropertyGroup>", sdk: "Microsoft.NET.Sdk.Web");
        var dll = Built(project, "Shop");

        var output = BuildOutputLocator.Find(new ClrProjectLink(project));

        Assert.Equal(dll, output.Assembly);
        Assert.Equal(["Microsoft.AspNetCore.App"], output.SharedFrameworks);
        Assert.Contains("\"Microsoft.AspNetCore.App\"", ModelHostRunner.RuntimeConfig(output));
    }

    [Fact]
    public void Missing_project_missing_build_and_old_frameworks_are_explained()
    {
        Assert.Equal(ClrModelErrorKind.ProjectNotFound,
            Assert.Throws<ClrModelException>(() => BuildOutputLocator.Find(new ClrProjectLink(Path.Combine(_root.FullName, "x.csproj")))).Kind);

        var notBuilt = Project("Shop.Neu");
        var error = Assert.Throws<ClrModelException>(() => BuildOutputLocator.Find(new ClrProjectLink(notBuilt)));
        Assert.Equal(ClrModelErrorKind.NotBuilt, error.Kind);
        Assert.Contains("bitte das Projekt bauen", error.Message);

        var old = Project("Shop.Alt");
        Built(old, "Shop.Alt", ".NETCoreApp,Version=v5.0", tfm: "net5.0");
        Assert.Contains("ab EF Core 8", Assert.Throws<ClrModelException>(() => BuildOutputLocator.Find(new ClrProjectLink(old))).Message);

        var standard = Project("Shop.Std");
        Built(standard, "Shop.Std", ".NETStandard,Version=v2.0", tfm: "netstandard2.0");
        Assert.Equal(ClrModelErrorKind.UnsupportedFramework, Assert.Throws<ClrModelException>(() => BuildOutputLocator.Find(new ClrProjectLink(standard))).Kind);
    }

    [Fact]
    public void The_runtime_config_rolls_forward_to_a_newer_runtime()
    {
        var output = new BuildOutput("a.dll", "a.deps.json", new Version(9, 0), DateTime.UtcNow, [], [], null);

        Assert.Equal(
            """{"runtimeOptions":{"tfm":"net9.0","rollForward":"Major","frameworks":[{"name":"Microsoft.NETCore.App","version":"9.0.0"}]}}""",
            ModelHostRunner.RuntimeConfig(output));
    }
}
