using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using FerretSharp.App.Services;
using FerretSharp.Core;
using FerretSharp.Core.IO;
using FerretSharp.Core.Resources;
using FerretSharp.Core.Settings;
using FerretSharp.UI.Resources;
using Velopack;

namespace FerretSharp.App;

/// <summary>Own entry point (WP-32): Velopack first, then one instance per data folder, then the WPF app.</summary>
internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Velopack's hooks (install, update, uninstall) start the app with arguments of their own and end it here, so this
        // comes before anything else (ADR 0024). A download is installed only on the user's click, never at a start.
        // WP-37: the installer registers ferretsharp:// for the current user and removes it again (ADR 0026). An update keeps
        // the program path (current\FerretSharp.exe); it registers only if nothing is, so a copy chosen in the settings stays.
        VelopackApp.Build()
            .SetAutoApplyOnStartup(false)
            .OnAfterInstallFastCallback(_ => RegisterLinks())
            .OnAfterUpdateFastCallback(_ => RegisterLinks(onlyIfNone: true))
            .OnBeforeUninstallFastCallback(_ => UnregisterLinks())
            .Run();

        var paths = DataFolder(args);
        WaitForPreviousInstance(args);
        InstanceLock? instance = null;
        try
        {
            if (!InstanceLock.TryAcquire(paths, Environment.ProcessId, out instance, out var running))
            {
                // A clicked link goes to the running instance (WP-37), which asks before opening it.
                var link = CommandLine.LinkOf(args);
                var handedOver = link is not null && LinkPipe.TrySend(paths, link);
                if (!RunningInstance.BringForward(running) && !handedOver)
                {
                    MessageBox.Show(TextFormat.Format(CommonText.AlreadyRunning, paths.Root), "FerretSharp", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else if (link is not null && !handedOver)
                {
                    MessageBox.Show(CommonText.LinkNotHandedOver, "FerretSharp", MessageBoxButton.OK, MessageBoxImage.Information);
                }

                return 0;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The data folder cannot be written: the start reports that itself (logs, settings), with or without the lock.
        }

        using (instance)
        {
            var app = new App(paths);
            app.InitializeComponent();
            return app.Run();
        }
    }

    private static void RegisterLinks(bool onlyIfNone = false)
    {
        if (Environment.ProcessPath is { } program && (!onlyIfNone || new ProtocolRegistration().RegisteredProgram is null))
        {
            ProtocolRegistration.Register(program);
        }
    }

    private static void UnregisterLinks()
    {
        if (Environment.ProcessPath is { } program)
        {
            ProtocolRegistration.Unregister(program);
        }
    }

    /// <summary>
    /// A restart after the graphics output failed (<see cref="CommandLine.WaitFor"/>) starts while the old instance still
    /// closes its connections and holds the instance lock: wait for it, at most 30 s (then the lock decides as usual).
    /// </summary>
    private static void WaitForPreviousInstance(string[] args)
    {
        if (!int.TryParse(CommandLine.ValueOf(args, CommandLine.WaitFor), NumberStyles.None, CultureInfo.InvariantCulture, out var id))
        {
            return;
        }

        try
        {
            using var previous = Process.GetProcessById(id);
            previous.WaitForExit(TimeSpan.FromSeconds(30));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // ended already, or not ours to wait for
        }
    }

    /// <summary>"--data-dir=&lt;path&gt;" redirects connections, workspaces and logs (tests, screenshots, separate profiles).</summary>
    private static AppPaths DataFolder(string[] args)
    {
        var dataDir = CommandLine.ValueOf(args, CommandLine.DataDir);
        return string.IsNullOrWhiteSpace(dataDir) ? AppPaths.Default : new AppPaths(Path.GetFullPath(dataDir));
    }
}
