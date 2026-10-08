using System.Diagnostics;
using System.Text;

namespace FerretSharp.Core.ClrModel;

/// <summary>Exit code and output (stdout and stderr interleaved) of a <c>dotnet</c> call.</summary>
public sealed record DotNetRun(int ExitCode, string Output)
{
    public bool Succeeded => ExitCode == 0;
}

/// <summary>Runs the installed <c>dotnet</c> (FerretSharp itself is self-contained and brings none).</summary>
public static class DotNetCli
{
    private const int MaxOutput = 200_000;

    /// <summary>Starts <c>dotnet</c> and returns while it runs (the LINQ console); lines arrive on any thread.</summary>
    public static Process Start(IEnumerable<string> arguments, string workingDirectory, Action<string> onOutputLine, Action<string> onErrorLine)
    {
        var process = new Process { StartInfo = StartInfo(arguments, workingDirectory) };
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is { } line)
            {
                onOutputLine(line);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is { } line)
            {
                onErrorLine(line);
            }
        };
        if (!process.Start())
        {
            process.Dispose();
            throw new ClrModelException(ClrModelErrorKind.DotNetMissing, "dotnet ließ sich nicht starten.");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    private static ProcessStartInfo StartInfo(IEnumerable<string> arguments, string workingDirectory)
    {
        var start = new ProcessStartInfo(FindDotNet())
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        // English tool messages, no telemetry banner, no first-run experience in the output.
        start.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en";
        start.Environment["DOTNET_NOLOGO"] = "1";
        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        return start;
    }

    public static async Task<DotNetRun> RunAsync(
        IEnumerable<string> arguments, string workingDirectory, TimeSpan timeout, CancellationToken cancellationToken, Action<string>? onOutputLine = null)
    {
        using var process = new Process { StartInfo = StartInfo(arguments, workingDirectory) };
        var output = new StringBuilder();
        void Append(string? line)
        {
            if (line is null)
            {
                return;
            }

            lock (output)
            {
                if (output.Length < MaxOutput)
                {
                    output.AppendLine(line);
                }
            }
        }

        process.OutputDataReceived += (_, e) =>
        {
            Append(e.Data);
            if (e.Data is { } line)
            {
                onOutputLine?.Invoke(line);
            }
        };
        process.ErrorDataReceived += (_, e) => Append(e.Data);
        if (!process.Start())
        {
            throw new ClrModelException(ClrModelErrorKind.DotNetMissing, "dotnet ließ sich nicht starten.");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token);
        }
        catch (OperationCanceledException)
        {
            KillTree(process);
            cancellationToken.ThrowIfCancellationRequested();
            string captured;
            lock (output)
            {
                captured = output.ToString();
            }

            // What it wrote up to then says where it hung (restore, a build step, the project's code).
            throw new ClrModelException(ClrModelErrorKind.Timeout, $"dotnet hat nach {timeout.TotalSeconds:0} s nicht geantwortet und wurde beendet.", captured);
        }

        process.WaitForExit(); // flushes the asynchronous output readers
        lock (output)
        {
            return new DotNetRun(process.ExitCode, output.ToString());
        }
    }

    /// <summary>
    /// Ends the process and everything it started (MSBuild nodes, the project's code) and waits briefly for it to go. Never
    /// throws: it may have exited already, or Windows may refuse to end a child.
    /// </summary>
    public static void KillTree(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit(2000);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or AggregateException or NotSupportedException)
        {
            // already exited, or a child that is not ours to end
        }
    }

    /// <summary>The <c>dotnet</c> muxer: <c>DOTNET_HOST_PATH</c>, the PATH, then the default install location.</summary>
    public static string FindDotNet()
    {
        var name = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        if (Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host && File.Exists(host))
        {
            return host;
        }

        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory.Trim('"'), name);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        var installed = OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", name)
            : "/usr/share/dotnet/dotnet";
        return File.Exists(installed)
            ? installed
            : throw new ClrModelException(ClrModelErrorKind.DotNetMissing,
                "dotnet wurde nicht gefunden – für das C#-Modell muss ein .NET SDK oder eine .NET-Runtime installiert sein.");
    }
}
