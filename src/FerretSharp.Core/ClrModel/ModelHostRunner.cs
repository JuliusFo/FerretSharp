using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FerretSharp.Core.ClrModel;

/// <summary>Reads the EF Core model of a linked project (ADR 0009).</summary>
public interface IModelHostRunner
{
    /// <summary>Runs the model host on the build output; failures come back as <see cref="ModelHostResult.Error"/>.</summary>
    /// <param name="progress">The step the host is in ('Baue das Modell (OnModelCreating)'); may be called on any thread.</param>
    Task<ModelHostResult> ReadModelAsync(ClrProjectLink link, BuildOutput output, CancellationToken cancellationToken, IProgress<string>? progress = null);

    /// <summary><c>dotnet build</c> of the linked project in its configuration.</summary>
    Task<DotNetRun> BuildAsync(ClrProjectLink link, CancellationToken cancellationToken);

    /// <summary>
    /// Starts the model host as LINQ console (ADR 0011) and waits until it has loaded the project and built the model.
    /// </summary>
    /// <exception cref="ClrModelException">The host could not start or reported an error while loading.</exception>
    Task<ILinqConsole> StartConsoleAsync(ClrProjectLink link, BuildOutput output, CancellationToken cancellationToken, IProgress<string>? progress = null);
}

/// <summary>Exit code and output (stdout and stderr interleaved) of a <c>dotnet</c> call.</summary>
public sealed record DotNetRun(int ExitCode, string Output)
{
    public bool Succeeded => ExitCode == 0;
}

/// <summary>
/// Starts <c>FerretSharp.ModelHost</c> like <c>dotnet ef</c> starts its tool: <c>dotnet exec</c> with the project's
/// deps.json, a runtimeconfig for the project's runtime and the NuGet package folders as probing paths. So the project's
/// runtime, EF Core and Oracle provider are used, not FerretSharp's.
/// </summary>
/// <param name="modelHostPath">Path of <c>FerretSharp.ModelHost.dll</c> (shipped with FerretSharp).</param>
/// <param name="culture">
/// Language of the enum display names the host reads from the project's resources; FerretSharp's UI culture by default.
/// </param>
public sealed class ModelHostRunner(string modelHostPath, TimeSpan? timeout = null, CultureInfo? culture = null) : IModelHostRunner
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(2);

    private readonly TimeSpan _timeout = timeout ?? DefaultTimeout;

    public async Task<ModelHostResult> ReadModelAsync(ClrProjectLink link, BuildOutput output, CancellationToken cancellationToken, IProgress<string>? progress = null)
    {
        if (!File.Exists(modelHostPath))
        {
            return Fail(ClrModelErrorKind.HostFailed, $"FerretSharp.ModelHost fehlt: {modelHostPath}");
        }

        var work = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "FerretSharp", "modelhost", Guid.NewGuid().ToString("N")));
        try
        {
            var runtimeConfig = Path.Combine(work.FullName, "modelhost.runtimeconfig.json");
            await File.WriteAllTextAsync(runtimeConfig, RuntimeConfig(output), cancellationToken);
            var result = Path.Combine(work.FullName, "model.json");

            var arguments = HostArguments(link, output, runtimeConfig, ["--output", result]);

            progress?.Report("Starte den Hilfsprozess");
            var run = await DotNetCli.RunAsync(arguments, Path.GetDirectoryName(output.Assembly)!, _timeout, cancellationToken, line =>
            {
                if (line.StartsWith(ModelHostResult.ProgressPrefix, StringComparison.Ordinal))
                {
                    progress?.Report(line[ModelHostResult.ProgressPrefix.Length..]);
                }
            });
            if (File.Exists(result))
            {
                await using var stream = File.OpenRead(result);
                var parsed = await JsonSerializer.DeserializeAsync<ModelHostResult>(stream, ModelHostResult.JsonOptions, cancellationToken);
                if (parsed is not null)
                {
                    return parsed;
                }
            }

            return Fail(ClrModelErrorKind.HostFailed, $"FerretSharp.ModelHost ist ohne Ergebnis beendet (Exit-Code {run.ExitCode}).", run.Output);
        }
        catch (ClrModelException ex)
        {
            return new ModelHostResult(null, ex.ToError());
        }
        finally
        {
            try
            {
                work.Delete(recursive: true);
            }
            catch (IOException)
            {
                // a file still in use by a killed process: the temp folder is cleaned up by Windows later
            }
        }
    }

    public async Task<ILinqConsole> StartConsoleAsync(ClrProjectLink link, BuildOutput output, CancellationToken cancellationToken, IProgress<string>? progress = null)
    {
        if (!File.Exists(modelHostPath))
        {
            throw new ClrModelException(ClrModelErrorKind.HostFailed, $"FerretSharp.ModelHost fehlt: {modelHostPath}");
        }

        var work = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "FerretSharp", "modelhost", Guid.NewGuid().ToString("N")));
        var runtimeConfig = Path.Combine(work.FullName, "modelhost.runtimeconfig.json");
        await File.WriteAllTextAsync(runtimeConfig, RuntimeConfig(output), cancellationToken);
        var pipe = "ferretsharp-linq-" + Guid.NewGuid().ToString("N");
        return await LinqConsoleHost.StartAsync(
            HostArguments(link, output, runtimeConfig, ["--console", pipe]), Path.GetDirectoryName(output.Assembly)!, pipe, work, output,
            _timeout, progress, cancellationToken);
    }

    /// <summary><c>dotnet exec</c> with the project's deps.json and runtime, then the host and its arguments.</summary>
    private List<string> HostArguments(ClrProjectLink link, BuildOutput output, string runtimeConfig, IEnumerable<string> mode)
    {
        var arguments = new List<string> { "exec", "--runtimeconfig", runtimeConfig, "--depsfile", output.DepsFile };
        foreach (var folder in output.PackageFolders)
        {
            arguments.Add("--additionalprobingpath");
            arguments.Add(folder);
        }

        arguments.AddRange([modelHostPath, "--assembly", output.Assembly, .. mode, "--culture", (culture ?? CultureInfo.CurrentUICulture).Name]);
        if (link.ContextType is { Length: > 0 } context)
        {
            arguments.AddRange(["--context", context]);
        }

        return arguments;
    }

    public Task<DotNetRun> BuildAsync(ClrProjectLink link, CancellationToken cancellationToken) =>
        DotNetCli.RunAsync(["build", link.ProjectFile, "-c", link.Configuration, "-nologo", "-v", "q"],
            Path.GetDirectoryName(link.ProjectFile)!, TimeSpan.FromMinutes(10), cancellationToken);

    /// <summary>
    /// The project's runtime (a class library has no runtimeconfig of its own), rolling forward to a newer one if only
    /// that is installed.
    /// </summary>
    internal static string RuntimeConfig(BuildOutput output)
    {
        var version = $"{output.TargetFramework.Major}.{output.TargetFramework.Minor}.0";
        var frameworks = new JsonArray(new JsonObject { ["name"] = "Microsoft.NETCore.App", ["version"] = version });
        foreach (var shared in output.SharedFrameworks)
        {
            frameworks.Add(new JsonObject { ["name"] = shared, ["version"] = version });
        }

        var options = new JsonObject
        {
            ["tfm"] = string.Create(CultureInfo.InvariantCulture, $"net{output.TargetFramework.Major}.{output.TargetFramework.Minor}"),
            ["rollForward"] = "Major",
            ["frameworks"] = frameworks,
        };
        return new JsonObject { ["runtimeOptions"] = options }.ToJsonString();
    }

    private static ModelHostResult Fail(string kind, string message, string? detail = null) => new(null, new ModelHostError(kind, message, detail));
}

/// <summary>Runs the installed <c>dotnet</c> (FerretSharp itself is self-contained and brings none).</summary>
public static class DotNetCli
{
    private const int MaxOutput = 200_000;

    /// <param name="onOutputLine">Called for every line on stdout as it arrives (any thread).</param>
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
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // already exited
            }

            cancellationToken.ThrowIfCancellationRequested();
            throw new ClrModelException(ClrModelErrorKind.Timeout, $"dotnet hat nach {timeout.TotalSeconds:0} s nicht geantwortet und wurde beendet.");
        }

        process.WaitForExit(); // flushes the asynchronous output readers
        lock (output)
        {
            return new DotNetRun(process.ExitCode, output.ToString());
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
