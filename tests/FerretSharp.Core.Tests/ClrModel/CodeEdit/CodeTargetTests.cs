using System.Diagnostics;
using FerretSharp.Core.ClrModel;
using FerretSharp.Core.IO;
using FerretSharp.Testing;

namespace FerretSharp.Core.Tests.ClrModel;

/// <summary>The warnings before writing into a work clone (WP-41): the branch of a connection, uncommitted changes.</summary>
public sealed class CodeTargetTests
{
    private static string? Git => ExecutableSearch.FindOnPath(OperatingSystem.IsWindows() ? "git.exe" : "git");

    private static void Run(string folder, params string[] arguments)
    {
        var start = new ProcessStartInfo(Git!) { WorkingDirectory = folder, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in (string[])["-c", "user.name=Test", "-c", "user.email=test@example.invalid", "-c", "commit.gpgsign=false", .. arguments])
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }

    private static string Repository(TestFolder folder, string name, string branch)
    {
        var path = folder.Combine(name);
        Directory.CreateDirectory(path);
        Run(path, "init", "-q", "-b", branch);
        File.WriteAllText(Path.Combine(path, "A.cs"), "class A {}");
        File.WriteAllText(Path.Combine(path, "B.cs"), "class B {}");
        File.WriteAllText(Path.Combine(path, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        Run(path, "add", ".");
        Run(path, "commit", "-q", "-m", "start");
        return path;
    }

    private static FileChange Change(string root, string name) =>
        new(Path.Combine(root, name), name, CodeFile.Read(Path.Combine(root, name))!, "class X {}", CodeFileFormat.Default);

    [Fact]
    public async Task A_work_clone_on_a_connections_branch_and_a_file_with_uncommitted_changes_are_warned_about()
    {
        Assert.SkipWhen(Git is null, "git is not installed");
        using var folder = new TestFolder();
        var work = Repository(folder, "work", "dev");
        var linked = Repository(folder, "linked", "dev");
        File.WriteAllText(Path.Combine(work, "B.cs"), "class B { int typed; }");
        var plan = new CodeChangePlan(work, [Change(work, "A.cs"), Change(work, "B.cs")], [], []);

        var check = await CodeTarget.CheckAsync(plan, [Path.Combine(linked, "App.csproj")], TestContext.Current.CancellationToken);

        Assert.Equal("dev", check.Branch);
        Assert.Equal(
        [
            "Der Arbeits-Klon steht auf dev – dem Branch des verknüpften Klons einer Verbindung. Nur schreiben, wenn das gewollt ist.",
            "B.cs hat nicht committete Änderungen.",
        ], check.Warnings);
    }

    /// <summary>Review WP-41, K10: git refusing (no repository, safe.directory) was reported as "git not found".</summary>
    [Fact]
    public async Task Git_that_does_not_answer_for_the_folder_is_not_reported_as_missing()
    {
        Assert.SkipWhen(Git is null, "git is not installed");
        using var folder = new TestFolder();
        File.WriteAllText(folder.Combine("A.cs"), "class A {}");
        var plan = new CodeChangePlan(folder.Path, [Change(folder.Path, "A.cs")], [], []);

        var check = await CodeTarget.CheckAsync(plan, [], TestContext.Current.CancellationToken);

        Assert.Equal(["Git hat für den Arbeits-Klon nicht geantwortet: Nicht committete Änderungen der Dateien werden nicht geprüft."], check.Warnings);
    }

    [Fact]
    public async Task The_linked_clone_itself_and_another_branch_are_no_warning()
    {
        Assert.SkipWhen(Git is null, "git is not installed");
        using var folder = new TestFolder();
        var work = Repository(folder, "work", "feature/x");
        var linked = Repository(folder, "linked", "dev");
        var plan = new CodeChangePlan(work, [Change(work, "A.cs")], [], []);

        var check = await CodeTarget.CheckAsync(plan, [Path.Combine(linked, "App.csproj"), Path.Combine(work, "App.csproj")], TestContext.Current.CancellationToken);

        Assert.Empty(check.Warnings);
    }
}
