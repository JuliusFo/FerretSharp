using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FerretSharp.Core.IO;
using FerretSharp.Core.Resources;

namespace FerretSharp.Core.ClrModel;

/// <summary>Reads the EF Core model of a linked project (ADR 0009).</summary>
public interface IModelHostRunner
{
    /// <summary>Runs the model host on the build output; failures come back as <see cref="ModelHostResult.Error"/>.</summary>
    /// <param name="progress">The step the host is in ('Building the model (OnModelCreating)'); may be called on any thread.</param>
    Task<ModelHostResult> ReadModelAsync(ClrProjectLink link, BuildOutput output, CancellationToken cancellationToken, IProgress<string>? progress = null);

    /// <summary><c>dotnet build</c> of the linked project in its configuration.</summary>
    Task<DotNetRun> BuildAsync(ClrProjectLink link, CancellationToken cancellationToken);

    /// <summary>
    /// Starts the model host as LINQ console (ADR 0011) and waits until it has loaded the project and built the model.
    /// </summary>
    /// <exception cref="ClrModelException">The host could not start or reported an error while loading.</exception>
    Task<ILinqConsole> StartConsoleAsync(ClrProjectLink link, BuildOutput output, CancellationToken cancellationToken, IProgress<string>? progress = null);
}

/// <summary>
/// Starts <c>FerretSharp.ModelHost</c> like <c>dotnet ef</c> starts its tool: <c>dotnet exec</c> with the project's
/// deps.json, a runtimeconfig for the project's runtime and the NuGet package folders as probing paths. So the project's
/// runtime, EF Core and Oracle provider are used, not FerretSharp's.
/// </summary>
/// <param name="modelHostPath">Path of <c>FerretSharp.ModelHost.dll</c> (shipped with FerretSharp).</param>
/// <param name="culture">
/// Language of the enum display names the host reads from the project's resources (the app passes the Windows UI culture);
/// FerretSharp's UI culture by default. The host's own messages always follow FerretSharp's UI culture.
/// </param>
/// <param name="shadow">
/// Copies of the build output the host runs from, so a build of the project is never blocked by a DLL the host has
/// loaded (ADR 0016); null = run from the build output itself.
/// </param>
public sealed class ModelHostRunner(string modelHostPath, TimeSpan? timeout = null, CultureInfo? culture = null, BuildOutputShadow? shadow = null)
    : IModelHostRunner
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(2);

    private readonly TimeSpan _timeout = timeout ?? DefaultTimeout;

    public async Task<ModelHostResult> ReadModelAsync(ClrProjectLink link, BuildOutput output, CancellationToken cancellationToken, IProgress<string>? progress = null)
    {
        HostLaunch? launch = null;
        try
        {
            launch = await PrepareAsync(output, progress, cancellationToken);
            var result = Path.Combine(launch.Work.FullName, "model.json");
            var arguments = HostArguments(link, launch, ["--output", result]);

            progress?.Report(ClrModelText.StepStartHelper);
            var run = await DotNetCli.RunAsync(arguments, launch.AppDirectory, _timeout, cancellationToken, background: true, onOutputLine: line =>
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

            return Fail(ClrModelErrorKind.HostFailed, TextFormat.Format(ClrModelText.HostEndedWithoutResult, run.ExitCode), run.Output);
        }
        catch (ClrModelException ex)
        {
            return new ModelHostResult(null, ex.ToError());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or System.ComponentModel.Win32Exception)
        {
            // dotnet not startable, a temp file not writable, a truncated result: an error like the others, as promised
            return Fail(ClrModelErrorKind.HostFailed, TextFormat.Format(ClrModelText.HostNotRunnable, ex.Message));
        }
        finally
        {
            if (launch is not null)
            {
                launch.Lease?.Dispose();
                LinqConsoleHost.TryDelete(launch.Work);
            }
        }
    }

    public async Task<ILinqConsole> StartConsoleAsync(ClrProjectLink link, BuildOutput output, CancellationToken cancellationToken, IProgress<string>? progress = null)
    {
        var launch = await PrepareAsync(output, progress, cancellationToken); // from here on the console owns the folder and the copy
        var pipe = "ferretsharp-linq-" + Guid.NewGuid().ToString("N");
        return await LinqConsoleHost.StartAsync(
            HostArguments(link, launch, ["--console", pipe]), launch.AppDirectory, pipe, launch.Work, launch.Lease, output,
            _timeout, progress, cancellationToken);
    }

    /// <summary>Each start of the host gets a work folder of its own below this one; nothing else is ever deleted.</summary>
    internal static string WorkRoot { get; } = Path.Combine(Path.GetTempPath(), "FerretSharp", "modelhost");

    /// <summary>Set once old work folders were removed in this process.</summary>
    private static int _staleFoldersRemoved;

    /// <summary>
    /// Work folders of earlier runs that could not be deleted then (a file still held by a host that was being killed):
    /// removed once per process, if older than <paramref name="age"/> – a younger one may belong to another FerretSharp.
    /// </summary>
    internal static void DeleteStaleWorkFolders(string root, TimeSpan age)
    {
        try
        {
            if (!Directory.Exists(root))
            {
                return;
            }

            foreach (var folder in new DirectoryInfo(root).EnumerateDirectories())
            {
                if (DateTime.UtcNow - folder.LastWriteTimeUtc > age)
                {
                    SafeDelete.TryDirectoryBelow(root, folder.FullName);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // housekeeping only
        }
    }

    /// <summary>
    /// A start of the host: its own temp folder with the runtimeconfig for the project's runtime, and the copy of the
    /// build output it runs from (the output itself without a shadow).
    /// </summary>
    private sealed record HostLaunch(
        DirectoryInfo Work, string RuntimeConfig, ShadowLease? Lease, string AppDirectory, string Assembly, string DepsFile, IReadOnlyList<string> PackageFolders);

    /// <summary>
    /// Checks the host is there, writes the runtimeconfig into a new temp folder (R2: was in both starts) and takes a copy
    /// of the build output; the folder is removed again if that fails.
    /// </summary>
    /// <exception cref="ClrModelException">FerretSharp.ModelHost is missing, or the output could not be copied.</exception>
    private async Task<HostLaunch> PrepareAsync(BuildOutput output, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        if (!File.Exists(modelHostPath))
        {
            throw new ClrModelException(ClrModelErrorKind.HostFailed, TextFormat.Format(ClrModelText.HostMissing, modelHostPath));
        }

        var root = WorkRoot;
        if (Interlocked.Exchange(ref _staleFoldersRemoved, 1) == 0)
        {
            DeleteStaleWorkFolders(root, TimeSpan.FromDays(1));
        }

        var work = Directory.CreateDirectory(Path.Combine(root, Guid.NewGuid().ToString("N")));
        var runtimeConfig = Path.Combine(work.FullName, "modelhost.runtimeconfig.json");
        try
        {
            await File.WriteAllTextAsync(runtimeConfig, RuntimeConfig(output), cancellationToken);
            if (shadow is null)
            {
                return new HostLaunch(work, runtimeConfig, null, Path.GetDirectoryName(output.Assembly)!, output.Assembly, output.DepsFile, output.PackageFolders);
            }

            var lease = await shadow.AcquireAsync(output, progress, cancellationToken);
            return new HostLaunch(work, runtimeConfig, lease, lease.Directory, lease.Assembly, lease.DepsFile, output.PackageFolders);
        }
        catch
        {
            LinqConsoleHost.TryDelete(work);
            throw;
        }
    }

    /// <summary><c>dotnet exec</c> with the project's deps.json and runtime, then the host and its arguments.</summary>
    private List<string> HostArguments(ClrProjectLink link, HostLaunch launch, IEnumerable<string> mode)
    {
        var arguments = new List<string> { "exec", "--runtimeconfig", launch.RuntimeConfig, "--depsfile", launch.DepsFile };
        foreach (var folder in launch.PackageFolders)
        {
            arguments.Add("--additionalprobingpath");
            arguments.Add(folder);
        }

        // --culture: the project's [Display] resources (the Windows language); --ui-culture: the host's own messages and
        // steps, in FerretSharp's UI language at the time of the call (WP-29, ADR 0017).
        arguments.AddRange([modelHostPath, "--assembly", launch.Assembly, .. mode, "--culture", (culture ?? CultureInfo.CurrentUICulture).Name,
            "--ui-culture", CultureInfo.CurrentUICulture.Name]);
        if (link.ContextType is { Length: > 0 } context)
        {
            arguments.AddRange(["--context", context]);
        }

        return arguments;
    }

    public Task<DotNetRun> BuildAsync(ClrProjectLink link, CancellationToken cancellationToken) =>
        DotNetCli.RunAsync(["build", link.ProjectFile, "-c", link.Configuration, "-nologo", "-v", "q", "-nodeReuse:false"],
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
