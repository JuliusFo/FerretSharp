using System.Diagnostics;
using FerretSharp.Core.IO;
using FerretSharp.Testing;

namespace FerretSharp.UI.Tests;

/// <summary>
/// The tests of the pure JavaScript modules (<c>tests/js</c>, ADR 0031; review 3.44.0, C5: the keyboard selection of the
/// grid had none) run with <c>node --test</c>. Without Node they are skipped – in the CI, where Node is on every runner,
/// that fails the run.
/// </summary>
public sealed class JsTests
{
    [Fact]
    public async Task The_javascript_modules_pass_their_tests()
    {
        var node = ExecutableSearch.FindOnPath(OperatingSystem.IsWindows() ? "node.exe" : "node");
        if (node is null)
        {
            Assert.False(string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase), "Node is missing on the CI runner.");
            Assert.Skip("Node is not on the PATH.");
        }

        var start = new ProcessStartInfo(node) { WorkingDirectory = Repository.Root, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("--test");
        foreach (var file in Directory.GetFiles(Path.Combine(Repository.Root, "tests", "js"), "*.test.mjs").Order(StringComparer.Ordinal))
        {
            start.ArgumentList.Add(file);
        }

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var errors = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken);

        Assert.True(process.ExitCode == 0, await output + await errors);
    }
}
