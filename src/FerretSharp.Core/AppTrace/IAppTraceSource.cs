namespace FerretSharp.Core.AppTrace;

/// <summary>A local .NET process that can be traced (WP-33).</summary>
/// <param name="CommandLine">Null if it could not be read.</param>
/// <param name="StartTime">Null if it could not be read.</param>
public sealed record TraceableProcess(int Id, string Name, string? CommandLine, DateTimeOffset? StartTime)
{
    /// <summary>The environment from the command line (<see cref="AppEnvironment"/>); null if none.</summary>
    public string? Environment => AppEnvironment.FromCommandLine(CommandLine);

    /// <summary>
    /// The name to show: the process name, but for an app started by the <c>dotnet</c> host (<c>dotnet App.dll</c>,
    /// <c>dotnet exec</c>) the name of its first .dll argument.
    /// </summary>
    public static string NameOf(string processName, string? commandLine)
    {
        if (!processName.Equals("dotnet", StringComparison.OrdinalIgnoreCase) || commandLine is null)
        {
            return processName;
        }

        return AppEnvironment.Split(commandLine).Skip(1).FirstOrDefault(a => a.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) is { } dll
            ? Path.GetFileNameWithoutExtension(dll.Replace('\\', '/').Split('/')[^1])
            : processName;
    }
}

/// <summary>
/// Reads the EF Core events of a running app from outside (WP-33, ADR 0023): no code in the app, only processes of the
/// same user on this machine. Implemented over EventPipe in <c>FerretSharp.AppTrace</c>.
/// </summary>
public interface IAppTraceSource
{
    /// <summary>The local .NET processes that offer a diagnostics port (without this one), by name.</summary>
    IReadOnlyList<TraceableProcess> ListProcesses();

    /// <summary>
    /// Attaches to the process and yields its events (<see cref="EfEvents"/>) until it ends or <paramref name="ct"/> is
    /// canceled – both end the sequence without an exception. Attaching fails with <see cref="Connections.RefusedException"/>
    /// (the process is gone, has diagnostics switched off, or belongs to another user).
    /// </summary>
    IAsyncEnumerable<EfEvent> AttachAsync(int processId, CancellationToken ct);
}
