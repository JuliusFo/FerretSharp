using System.Diagnostics;

namespace FerretSharp.Core.ClrModel;

/// <summary>
/// The processes FerretSharp starts (model host, LINQ console, <c>dotnet build</c>) end with it (review 3.40.0, A18). Before,
/// a crash or a killed FerretSharp left them running: the model export without a time limit, a build for up to ten minutes,
/// and a host that kept DLLs of the installation open against the next update. Windows ends them with the process only if
/// they are in a job object – Windows-only, so the host sets <see cref="Adopt"/> at startup (<c>App/Services/ChildJob</c>);
/// without it (tests, Linux) nothing happens.
/// </summary>
public static class ChildProcesses
{
    /// <summary>Called right after each start of a child process; must not throw.</summary>
    public static Action<Process>? Adopt { get; set; }

    internal static void Started(Process process)
    {
        try
        {
            Adopt?.Invoke(process);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // exited already, or Windows refused: it runs as before
        }
    }
}
