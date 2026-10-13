using System.IO;
using System.Windows;
using FerretSharp.App.Services;
using FerretSharp.Core;
using FerretSharp.Core.IO;
using FerretSharp.Core.Resources;
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
        VelopackApp.Build().SetAutoApplyOnStartup(false).Run();

        var paths = DataFolder(args);
        InstanceLock? instance = null;
        try
        {
            if (!InstanceLock.TryAcquire(paths, Environment.ProcessId, out instance, out var running))
            {
                if (!RunningInstance.BringForward(running))
                {
                    MessageBox.Show(TextFormat.Format(CommonText.AlreadyRunning, paths.Root), "FerretSharp", MessageBoxButton.OK, MessageBoxImage.Information);
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

    /// <summary>"--data-dir=&lt;path&gt;" redirects connections, workspaces and logs (tests, screenshots, separate profiles).</summary>
    private static AppPaths DataFolder(string[] args)
    {
        var dataDir = args.FirstOrDefault(a => a.StartsWith("--data-dir=", StringComparison.OrdinalIgnoreCase))?["--data-dir=".Length..];
        return string.IsNullOrWhiteSpace(dataDir) ? AppPaths.Default : new AppPaths(Path.GetFullPath(dataDir));
    }
}
