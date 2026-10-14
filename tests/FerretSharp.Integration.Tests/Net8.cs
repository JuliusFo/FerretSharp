using System.Diagnostics;

namespace FerretSharp.Integration.Tests;

/// <summary>
/// The parts that run in .NET 8 (ModelHost, FakeEfApp) need that runtime and their build. Without either the tests skip
/// with the reason – except in the CI, which installs .NET 8 and must not turn a broken build into green skips.
/// </summary>
internal static class Net8
{
    private static readonly Lazy<string?> MissingRuntime = new(FindMissingRuntime);

    /// <summary>The path of a .NET 8 assembly the test runs; skips the test if it is not built or .NET 8 is missing.</summary>
    public static string Require(string assembly)
    {
        if (OracleContainerFixture.InCi)
        {
            return assembly;
        }

        Assert.SkipUnless(File.Exists(assembly), $"Not built: {assembly}");
        Assert.SkipWhen(MissingRuntime.Value is not null, MissingRuntime.Value ?? "");
        return assembly;
    }

    private static string? FindMissingRuntime()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("dotnet", "--list-runtimes")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
            })!;
            var runtimes = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            return runtimes.Split('\n').Any(line => line.StartsWith("Microsoft.NETCore.App 8.", StringComparison.Ordinal))
                ? null
                : "The .NET 8 runtime is not installed (dotnet --list-runtimes).";
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return $"dotnet could not be started: {ex.Message}";
        }
    }
}
