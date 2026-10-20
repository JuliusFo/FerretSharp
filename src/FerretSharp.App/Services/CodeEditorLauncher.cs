using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using FerretSharp.Core.Settings;
using FerretSharp.UI.Resources;
using FerretSharp.UI.State;
using Microsoft.Extensions.Logging;

namespace FerretSharp.App.Services;

/// <summary>
/// Opens a source file at a line (WP-40, ADR 0030). Visual Studio: the running instance with the solution open, found in
/// the Running Object Table and driven over COM by late binding (no EnvDTE package); otherwise <c>devenv /Edit</c>. VS Code
/// through <c>code -g</c>, anything else through the shell.
/// </summary>
public sealed class CodeEditorLauncher(ILogger<CodeEditorLauncher> logger) : ICodeEditorLauncher
{
    /// <summary>How long a call to a busy Visual Studio is retried (the spike saw it refuse right after OpenFile).</summary>
    private static readonly TimeSpan BusyLimit = TimeSpan.FromSeconds(5);

    public Task<CodeOpenResult> OpenAsync(CodeEditor editor, string file, int? line, string? solution, CancellationToken cancellationToken) =>
        Task.Run(() => editor switch
        {
            CodeEditor.VsCode => OpenInVsCode(file, line),
            CodeEditor.System => OpenWithShell(file),
            _ => OpenInVisualStudio(file, line, solution, cancellationToken),
        }, cancellationToken);

    private CodeOpenResult OpenInVisualStudio(string file, int? line, string? solution, CancellationToken cancellationToken)
    {
        try
        {
            if (FindInstance(file, solution) is { } dte)
            {
                try
                {
                    Retry(() => dte.ItemOperations.OpenFile(file), cancellationToken);
                    // Open now: a line or the window that fails stays a warning (review of WP-40: it fell back to devenv
                    // /Edit and opened the file a second time, e.g. for a line past the end of a shorter work clone file).
                    AfterOpen(() =>
                    {
                        if (line is { } at)
                        {
                            Retry(() => dte.ActiveDocument.Selection.GotoLine(at, true), cancellationToken);
                        }
                    }, "go to the line");
                    AfterOpen(() => Retry(() => dte.MainWindow.Activate(), cancellationToken), "bring Visual Studio to the front");
                    return new CodeOpenResult(CodeOpenOutcome.Opened);
                }
                finally
                {
                    Marshal.FinalReleaseComObject(dte);
                }
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or MissingMemberException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            logger.LogWarning(ex, "Opening a file in a running Visual Studio failed; falling back to devenv /Edit");
        }

        if (DevenvPath() is not { } devenv)
        {
            return new CodeOpenResult(CodeOpenOutcome.Failed, ModelViewText.Usage_NoVisualStudio);
        }

        return Start(new ProcessStartInfo(devenv) { ArgumentList = { "/Edit", file }, UseShellExecute = false }) is { } failure
            ? new CodeOpenResult(CodeOpenOutcome.Failed, failure)
            : new CodeOpenResult(CodeOpenOutcome.OpenedElsewhere);
    }

    private void AfterOpen(Action step, string what)
    {
        try
        {
            step();
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or MissingMemberException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            logger.LogWarning(ex, "Visual Studio opened the file but could not {Step}", what);
        }
    }

    /// <summary>
    /// The instance with exactly <paramref name="solution"/> open; else one whose solution folder contains the file; null
    /// if none (or none visible: an elevated Visual Studio is not in a normal user's Running Object Table).
    /// </summary>
    private dynamic? FindInstance(string file, string? solution)
    {
        var instances = Instances();
        dynamic? chosen = null;
        var bestDepth = -1;
        foreach (dynamic dte in instances)
        {
            string? open = null;
            try
            {
                open = (string)dte.Solution.FullName;
            }
            catch (Exception ex) when (ex is COMException or MissingMemberException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
            {
                // busy or closing: not this one
            }

            var depth = open is { Length: > 0 } ? Match(open, file, solution) : -1;
            if (depth > bestDepth)
            {
                if (chosen is not null)
                {
                    Marshal.FinalReleaseComObject(chosen);
                }

                (chosen, bestDepth) = (dte, depth);
            }
            else
            {
                Marshal.FinalReleaseComObject(dte);
            }
        }

        return chosen;
    }

    /// <summary>Higher is better: the solution itself beats a solution whose folder holds the file (the deeper, the better); -1 = no.</summary>
    private static int Match(string open, string file, string? solution)
    {
        if (solution is not null && string.Equals(Path.GetFullPath(open), Path.GetFullPath(solution), StringComparison.OrdinalIgnoreCase))
        {
            return int.MaxValue;
        }

        var folder = Path.GetDirectoryName(Path.GetFullPath(open));
        return folder is not null && Path.GetFullPath(file).StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            ? folder.Length
            : -1;
    }

    /// <summary>The DTE objects of the running Visual Studio instances (<c>!VisualStudio.DTE.18.0:1234</c>).</summary>
    private static List<object> Instances()
    {
        var result = new List<object>();
        if (GetRunningObjectTable(0, out var table) != 0 || CreateBindCtx(0, out var context) != 0)
        {
            return result;
        }

        try
        {
            table.EnumRunning(out var monikers);
            var buffer = new IMoniker[1];
            while (monikers.Next(1, buffer, IntPtr.Zero) == 0)
            {
                try
                {
                    buffer[0].GetDisplayName(context, null, out var name);
                    if (name.StartsWith("!VisualStudio.DTE.", StringComparison.Ordinal) && table.GetObject(buffer[0], out var dte) == 0)
                    {
                        result.Add(dte);
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(buffer[0]);
                }
            }

            Marshal.ReleaseComObject(monikers);
        }
        finally
        {
            Marshal.ReleaseComObject(context);
            Marshal.ReleaseComObject(table);
        }

        return result;
    }

    /// <summary>Retries a call Visual Studio refuses while it is busy; the late binder wraps the HRESULT in its message.</summary>
    private static void Retry(Action call, CancellationToken cancellationToken)
    {
        var started = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                call();
                return;
            }
            catch (Exception ex) when (IsBusy(ex) && started.Elapsed < BusyLimit)
            {
                cancellationToken.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(100));
                cancellationToken.ThrowIfCancellationRequested();
            }
        }
    }

    /// <summary>RPC_E_CALL_REJECTED, RPC_E_SERVERCALL_RETRYLATER.</summary>
    private static bool IsBusy(Exception ex) =>
        ex.HResult is unchecked((int)0x80010001) or unchecked((int)0x8001010A)
        || ex.Message.Contains("0x80010001", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("0x8001010A", StringComparison.OrdinalIgnoreCase);

    /// <summary>The newest Visual Studio's devenv.exe, from vswhere.</summary>
    private string? DevenvPath()
    {
        var vswhere = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft Visual Studio", "Installer", "vswhere.exe");
        if (!File.Exists(vswhere))
        {
            return null;
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo(vswhere)
            {
                ArgumentList = { "-latest", "-prerelease", "-products", "*", "-property", "productPath" },
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (process is null)
            {
                return null;
            }

            var path = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit(5000);
            return File.Exists(path) ? path : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            logger.LogWarning(ex, "vswhere failed");
            return null;
        }
    }

    private CodeOpenResult OpenInVsCode(string file, int? line)
    {
        var code = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Microsoft VS Code", "bin"))
            .Where(d => d.Length > 0)
            .Select(d => Path.Combine(d.Trim('"'), "code.cmd"))
            .FirstOrDefault(File.Exists);
        var start = VsCodeCommand.For(code, file, line);
        if (start.Program is not { } program)
        {
            return new CodeOpenResult(CodeOpenOutcome.Failed, start.Refusal);
        }

        var info = new ProcessStartInfo(program) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in start.Arguments)
        {
            info.ArgumentList.Add(argument);
        }

        return Start(info) is { } failure
            ? new CodeOpenResult(CodeOpenOutcome.Failed, failure)
            : new CodeOpenResult(CodeOpenOutcome.Opened);
    }

    private CodeOpenResult OpenWithShell(string file) =>
        Start(new ProcessStartInfo(file) { UseShellExecute = true }) is { } failure
            ? new CodeOpenResult(CodeOpenOutcome.Failed, failure)
            : new CodeOpenResult(CodeOpenOutcome.Opened);

    /// <summary>Starts the editor and lets it go (it is the user's program, not a child of FerretSharp); the error text or null.</summary>
    private string? Start(ProcessStartInfo start)
    {
        try
        {
            Process.Start(start)?.Dispose();
            return null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            logger.LogWarning(ex, "Starting {Program} failed", start.FileName);
            return ex.Message;
        }
    }

    [DllImport("ole32.dll")]
    private static extern int GetRunningObjectTable(int reserved, out IRunningObjectTable table);

    [DllImport("ole32.dll")]
    private static extern int CreateBindCtx(int reserved, out IBindCtx context);
}
