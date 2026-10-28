using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using FerretSharp.App.Services;
using FerretSharp.App.Views;
using FerretSharp.Core;
using FerretSharp.Core.ClrModel;
using FerretSharp.Core.Compare;
using FerretSharp.Core.Connections;
using FerretSharp.Core.Oracle;
using FerretSharp.Core.Query;
using FerretSharp.Core.Resources;
using FerretSharp.Core.Settings;
using FerretSharp.Core.Workspaces;
using FerretSharp.UI.Resources;
using FerretSharp.UI.State;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;

namespace FerretSharp.App;

public partial class App : Application
{
    private readonly AppPaths _paths;
    private readonly RenderFailureNotice _renderFailures = new();
    private readonly string[] _args;
    private IHost? _host;
    private ILogger<App>? _logger;
    private PowerModeChangedEventHandler? _powerModeChanged;

    /// <summary>Started by <see cref="Program.Main"/>, which holds the data folder's instance lock (WP-32).</summary>
    /// <param name="args">The arguments to act on (<see cref="CommandLine.Effective"/>), not WPF's own copy of the raw command line.</param>
    internal App(AppPaths paths, string[] args)
    {
        _paths = paths;
        _args = args;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var paths = _paths;
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Information)
            .WriteTo.File(
                Path.Combine(paths.LogsDirectory, "ferretsharp-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14)
            .CreateLogger();
        RegisterGlobalExceptionHandlers(); // before anything that can fail (R4a: a failing host build left no trace)
        // With the main window's close (MainWindow), the log tells why the app ended.
        SessionEnding += (_, args) => Log.Information("Windows session ending ({Reason})", args.ReasonSessionEnding);

        // Model host, LINQ console and builds end with FerretSharp, also after a crash (review 3.40.0, A18).
        ChildProcesses.Adopt = ChildJob.Create();
        if (ChildProcesses.Adopt is null)
        {
            Log.Warning("No job object for child processes: they may outlive FerretSharp");
        }

        // Settings decide the theme, which must be known before the window and the WebView exist (no white flash), and the
        // language. Read here, not in an async method: the UI culture belongs to the execution context, and a change made
        // inside an async method is undone when it returns – Blazor would render in the Windows language again.
        // A file that cannot be read is kept as a copy before the first save replaces it (R4a).
        var settingsService = Task.Run(() => AppSettingsService.OpenAsync(new SettingsStore(paths.SettingsFile), DateTimeOffset.Now, CancellationToken.None))
            .GetAwaiter().GetResult();
        var settings = settingsService.Current;

        // The UI language (WP-29, ADR 0017): only the UI culture changes, so numbers and dates keep their formats. The enum
        // display names of a linked C# project keep the Windows language (the projects may only have German resources).
        var windowsUiCulture = CultureInfo.CurrentUICulture;
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.CurrentUICulture =
            UiLanguages.Culture(UiLanguages.FromArguments(_args) ?? settings.Language);

        Start(_args, paths, settingsService, windowsUiCulture);
    }

    /// <summary>
    /// A start that fails says so and ends the process (R4a): before, the error was handled as one of a running app – with
    /// no main window yet, the process stayed alive without one (ShutdownMode OnMainWindowClose).
    /// </summary>
    private async void Start(string[] args, AppPaths paths, AppSettingsService settings, CultureInfo windowsUiCulture)
    {
        try
        {
            await StartAsync(args, paths, settings, windowsUiCulture);
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "FerretSharp could not start");
            MessageBox.Show(TextFormat.Format(CommonText.StartFailed, ex.Message, paths.LogsDirectory), "FerretSharp", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private async Task StartAsync(string[] args, AppPaths paths, AppSettingsService settings, CultureInfo windowsUiCulture)
    {
        var theme = WindowTheme.Create(args, settings.Current.Theme);

        // Without the defaults (review 3.40.0, A33): they read appsettings*.json from the working folder and watched it, took
        // DOTNET_ENVIRONMENT (scope validation only where it is "Development"), the arguments as configuration (a link too)
        // and logged to the console, the debug output and the Windows event log beside Serilog. Nothing here reads them.
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            DisableDefaults = true,
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.Services.AddSerilog();
        builder.Services.Configure<ConsoleLifetimeOptions>(o => o.SuppressStatusMessages = true);
        builder.Services.AddWpfBlazorWebView();
#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
#endif
        builder.Services.AddSingleton(paths);
        builder.Services.AddSingleton<IConnectionStore>(new ConnectionStore(paths.ConnectionsFile));
        builder.Services.AddSingleton<ISecretStore, CredentialManagerSecretStore>();
        builder.Services.AddSingleton<IConnectionTester, OracleConnectionTester>();
        builder.Services.AddSingleton<ConnectionManager>();
        // Import/export of connections (WP-35): DBeaver and SQL Developer are looked for in %APPDATA% (tests: --import-appdata=).
        builder.Services.AddSingleton(services => new ConnectionTransfer(services.GetRequiredService<ConnectionManager>(), ImportAppData(args)));
        builder.Services.AddSingleton(new RecentConnections(paths.RecentConnectionsFile));
        builder.Services.AddSingleton<IDatabaseConnector, OracleDatabaseConnector>();
        builder.Services.AddSingleton<IWorkspaceStore>(new WorkspaceStore(paths.WorkspacesDirectory));
        // Per open connection (WP-24): a DI scope each, created by the ConnectionHub; components get them cascaded.
        builder.Services.AddScoped<WorkspaceManager>();
        builder.Services.AddScoped<ActiveConnection>();
        builder.Services.AddSingleton<ConnectionHub>();
        builder.Services.AddSingleton<IOpenConnections>(services => services.GetRequiredService<ConnectionHub>());
        builder.Services.AddSingleton<ConnectionKeepAlive>();
        builder.Services.AddSingleton(settings);
        builder.Services.AddSingleton<ShortcutService>();
        builder.Services.AddSingleton<FerretSharp.Core.Audit.AuditTemplateSource>();
        // A factory, so the host disposes it (file watcher).
        builder.Services.AddSingleton(_ => new FerretSharp.Core.Display.DisplayColumnSource(settings, paths.DisplayColumnsFile));
        builder.Services.AddSingleton(theme);
        builder.Services.AddSingleton<IThemeService, ThemeService>();
        builder.Services.AddSingleton<IDialogService, DialogService>();
        builder.Services.AddSingleton<IFileSaveService, FileSaveService>();
        builder.Services.AddSingleton<IClipboardService, ClipboardService>();
        builder.Services.AddSingleton<IFileOpenService, FileOpenService>();
        builder.Services.AddSingleton<ICodeEditorLauncher, CodeEditorLauncher>();
        var modelHost = Path.Combine(AppContext.BaseDirectory, "modelhost", "FerretSharp.ModelHost.dll");
        // The host runs from copies of the build output, so the linked project can be built meanwhile (ADR 0016).
        var shadow = new BuildOutputShadow(Path.Combine(Path.GetTempPath(), AppPaths.AppFolderName, "shadow"));
        builder.Services.AddSingleton<IModelHostRunner>(new ModelHostRunner(modelHost, culture: windowsUiCulture, shadow: shadow));
        // Copies of projects no longer linked or builds long replaced; in the background, it never throws.
        _ = Task.Run(() => shadow.DeleteUnusedAsync(BuildOutputShadow.UnusedAfter, CancellationToken.None));
        // The caches up to 3.44 in the roaming folder (review 3.44.0, A11): built again where they live now.
        if (paths.FormerCacheDirectory is { } former)
        {
            _ = Task.Run(() => FerretSharp.Core.IO.SafeDelete.TryDirectoryBelow(paths.Root, former));
        }

        builder.Services.AddSingleton(new ModelCache(paths.ModelCacheDirectory, modelHost, windowsUiCulture));
        builder.Services.AddSingleton(new SqlHistoryStore(paths.SqlHistoryDirectory));
        builder.Services.AddSingleton(new ComparisonStore(paths.ComparisonsFile));
        builder.Services.AddSingleton<SchemaCompareLoader>();
        builder.Services.AddSingleton<SchemaCompareService>();
        // Filter presets (WP-37): one file for all connections.
        builder.Services.AddSingleton(new FilterPresetStore(paths.FilterPresetsFile));
        builder.Services.AddSingleton<FilterPresetService>();
        // Links (WP-37, ADR 0026): from the start argument and, from a second start, through the pipe.
        builder.Services.AddSingleton<LinkInbox>();
        builder.Services.AddSingleton<IProtocolRegistration, ProtocolRegistration>();
        builder.Services.AddHostedService<LinkPipeServer>();
        // App statements (WP-33): one trace for the whole app, over EventPipe.
        builder.Services.AddSingleton<FerretSharp.Core.AppTrace.IAppTraceSource, FerretSharp.AppTrace.EventPipeTraceSource>();
        builder.Services.AddSingleton<AppTraceService>();
        builder.Services.AddScoped<ClrModelManager>();
        builder.Services.AddScoped<PresentationService>();
        builder.Services.AddScoped<LinqConsoleService>();
        builder.Services.AddSingleton(new CodeUsageBuilder(paths.CodeUsageCacheDirectory));
        builder.Services.AddSingleton(new WorkClones(paths.WorkClonesFile));
        // Order and collapsed connection groups (WP-66), one state for the switcher and the connections page.
        builder.Services.AddSingleton(new ConnectionGroupState(paths.ConnectionGroupsFile));
        builder.Services.AddSingleton<ConnectionGroups>();
        builder.Services.AddScoped<CodeUsageService>();
        builder.Services.AddSingleton<CodeUsageOpener>();
        builder.Services.AddSingleton<ExitGuard>();
        // Updates (WP-32, ADR 0024): Velopack, only reports; installing waits for a click.
        builder.Services.AddSingleton<IAppUpdater>(new VelopackUpdater(args));
        builder.Services.AddSingleton<UpdateState>();
        builder.Services.AddSingleton<MainWindow>();
        builder.Services.AddSingleton(sp => new UiStallMonitor(
            Dispatcher, sp.GetRequiredService<ILogger<UiStallMonitor>>(), paths.LogsDirectory, sp.GetRequiredService<AppSettingsService>()));

        _host = builder.Build();
        _logger = _host.Services.GetRequiredService<ILogger<App>>();

        await _host.StartAsync();
        _logger.LogInformation("FerretSharp {Version} started", FerretSharp.UI.State.AppVersion.Current);
        if (OracleSession.BreaksInBand)
        {
            _logger.LogInformation("Breaks are sent in band ({Switch})", CommandLine.DisableOob);
        }
        if (settings.StartupNotice is { } notice)
        {
            _logger.LogWarning("Settings could not be read, using defaults: {Notice}", notice);
        }

        var window = _host.Services.GetRequiredService<MainWindow>();
        MainWindow = window;
        window.Show();
        if (CommandLine.LinkOf(args) is { } link)
        {
            _host.Services.GetRequiredService<LinkInbox>().Post(link); // the shell takes it once it has started
        }

        _host.Services.GetRequiredService<UiStallMonitor>().Start();
        WatchSleep(_host.Services.GetRequiredService<ConnectionKeepAlive>());
        _ = _host.Services.GetRequiredService<UpdateState>().StartAsync(); // never throws: a failed check is a note in the settings
    }

    /// <summary>
    /// Where the import looks for DBeaver and SQL Developer: <c>%APPDATA%</c>, or – for end-to-end tests, which must not
    /// see the user's real tools – the folder of <c>--import-appdata=&lt;path&gt;</c>.
    /// </summary>
    private static string ImportAppData(string[] args) =>
        CommandLine.ValueOf(args, CommandLine.ImportAppData) ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    /// <summary>
    /// After sleep the sessions are usually gone (the VPN dropped, the database timed them out): check them a few seconds after
    /// waking instead of up to two minutes later, so the banner – or reconnecting by itself – comes before the next click.
    /// </summary>
    private void WatchSleep(ConnectionKeepAlive keepAlive)
    {
        _powerModeChanged = (_, args) =>
        {
            Log.Information("Power mode {Mode}", args.Mode); // with the connect steps, the log tells what sleep did (2026-10-21)
            if (args.Mode == PowerModes.Resume)
            {
                keepAlive.CheckSoon();
            }
        };
        SystemEvents.PowerModeChanged += _powerModeChanged;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _logger?.LogInformation("FerretSharp shutting down");
        if (_powerModeChanged is not null)
        {
            SystemEvents.PowerModeChanged -= _powerModeChanged; // a static event: it would keep the app's objects alive
        }

        CloseConnection();

        // Synchronously, off the dispatcher and with a limit (review 3.31.0, A44): in the former async void OnExit the process
        // could end at the first await, so the services were never disposed – and a continuation that wanted the UI thread
        // could wait for it forever.
        if (_host is { } host)
        {
            try
            {
                Task.Run(async () =>
                {
                    try
                    {
                        await host.StopAsync(TimeSpan.FromSeconds(5));
                    }
                    finally
                    {
                        host.Dispose();
                    }
                }).Wait(TimeSpan.FromSeconds(8));
            }
            catch (AggregateException ex)
            {
                _logger?.LogWarning(ex, "Stopping the services failed");
            }
        }

        Log.CloseAndFlush();
        base.OnExit(e);
    }

    /// <summary>
    /// Saves the workspaces and closes all Oracle sessions of all open connections, and ends their LINQ helper processes.
    /// Runs synchronously (off the dispatcher) before anything is awaited in <see cref="OnExit"/>, because the process
    /// may end at the first await.
    /// </summary>
    private void CloseConnection()
    {
        if (_host?.Services.GetService<ConnectionHub>() is not { } hub)
        {
            return;
        }

        try
        {
            if (!Task.Run(() => hub.DisposeAsync().AsTask()).Wait(TimeSpan.FromSeconds(5)))
            {
                _logger?.LogWarning("Closing the connections did not finish within 5 seconds");
            }
        }
        catch (AggregateException ex)
        {
            _logger?.LogError(ex, "Closing the connection failed");
        }
    }

    private void RegisterGlobalExceptionHandlers()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        // Serilog directly: these may come before the host and its ILogger exist.
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Fatal(args.ExceptionObject as Exception, "Unhandled exception (terminating: {IsTerminating})", args.IsTerminating);

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error(args.Exception, "Unobserved task exception");
            args.SetObserved();
        };
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        if (RenderFailureNotice.Matches(e.Exception))
        {
            e.Handled = true;
            OnRenderThreadFailure(e.Exception);
            return;
        }

        Log.Error(e.Exception, "Unhandled exception on UI thread");
        _host?.Services.GetRequiredService<IDialogService>().ShowError(CommonText.UnexpectedError, e.Exception);
        e.Handled = true;
    }

    /// <summary>
    /// WPF's render thread failed – Windows reset the graphics output, e.g. when waking up at a dock. WPF reports it for every
    /// window message in that moment, and the app usually keeps working: one question per series whether to restart (an
    /// error dialog each time caused the next one), the rest only in the log.
    /// </summary>
    private void OnRenderThreadFailure(Exception exception)
    {
        if (!_renderFailures.Report())
        {
            Log.Warning("Render thread failed again ({Repeats} since the question)", _renderFailures.Repeats);
            return;
        }

        Log.Warning(exception, "Render thread failed (graphics output reset, e.g. after sleep at a dock)");
        // After the window message that failed: a dialog inside it would meet the render thread in the same moment.
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            bool restart;
            try
            {
                restart = _host?.Services.GetRequiredService<IDialogService>().Ask(CommonText.RenderFailure_Title, CommonText.RenderFailure_Question) == true;
            }
            finally
            {
                _renderFailures.Answered();
            }

            if (restart && _host is { } host)
            {
                // Like the window's close button: uncommitted changes are asked about first; cancelling keeps the app open.
                host.Services.GetRequiredService<ExitGuard>().RequestClose(StartAgain);
            }
        });
    }

    /// <summary>Starts FerretSharp as it was started; the new instance waits until this one has ended (instance lock).</summary>
    private void StartAgain()
    {
        try
        {
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
            foreach (var argument in CommandLine.Restart(_args, Environment.ProcessId))
            {
                start.ArgumentList.Add(argument);
            }

            _logger?.LogInformation("Restarting after the render thread failed");
            Process.Start(start)?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _logger?.LogError(ex, "Restarting failed");
        }
    }
}
