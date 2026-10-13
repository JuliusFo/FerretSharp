using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using FerretSharp.App.Services;
using FerretSharp.App.Views;
using FerretSharp.Core;
using FerretSharp.Core.ClrModel;
using FerretSharp.Core.Compare;
using FerretSharp.Core.Connections;
using FerretSharp.Core.Oracle;
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
    private IHost? _host;
    private ILogger<App>? _logger;

    /// <summary>Started by <see cref="Program.Main"/>, which holds the data folder's instance lock (WP-32).</summary>
    internal App(AppPaths paths) => _paths = paths;

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
            UiLanguages.Culture(UiLanguages.FromArguments(e.Args) ?? settings.Language);

        Start(e.Args, paths, settingsService, windowsUiCulture);
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

        var builder = Host.CreateApplicationBuilder(args);
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
        builder.Services.AddSingleton<IFileOpenService, FileOpenService>();
        var modelHost = Path.Combine(AppContext.BaseDirectory, "modelhost", "FerretSharp.ModelHost.dll");
        // The host runs from copies of the build output, so the linked project can be built meanwhile (ADR 0016).
        var shadow = new BuildOutputShadow(Path.Combine(Path.GetTempPath(), AppPaths.AppFolderName, "shadow"));
        builder.Services.AddSingleton<IModelHostRunner>(new ModelHostRunner(modelHost, culture: windowsUiCulture, shadow: shadow));
        // Copies of projects no longer linked or builds long replaced; in the background, it never throws.
        _ = Task.Run(() => shadow.DeleteUnusedAsync(BuildOutputShadow.UnusedAfter, CancellationToken.None));
        builder.Services.AddSingleton(new ModelCache(paths.ModelCacheDirectory, modelHost, windowsUiCulture));
        builder.Services.AddSingleton(new SqlHistoryStore(paths.SqlHistoryDirectory));
        builder.Services.AddSingleton(new ComparisonStore(paths.ComparisonsFile));
        builder.Services.AddSingleton<SchemaCompareLoader>();
        builder.Services.AddSingleton<SchemaCompareService>();
        // App statements (WP-33): one trace for the whole app, over EventPipe.
        builder.Services.AddSingleton<FerretSharp.Core.AppTrace.IAppTraceSource, FerretSharp.AppTrace.EventPipeTraceSource>();
        builder.Services.AddSingleton<AppTraceService>();
        builder.Services.AddScoped<ClrModelManager>();
        builder.Services.AddScoped<PresentationService>();
        builder.Services.AddScoped<LinqConsoleService>();
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
        _logger.LogInformation("FerretSharp {Version} started", typeof(App).Assembly.GetName().Version);
        if (settings.StartupNotice is { } notice)
        {
            _logger.LogWarning("Settings could not be read, using defaults: {Notice}", notice);
        }

        var window = _host.Services.GetRequiredService<MainWindow>();
        MainWindow = window;
        window.Show();
        _host.Services.GetRequiredService<UiStallMonitor>().Start();
        _ = _host.Services.GetRequiredService<UpdateState>().StartAsync(); // never throws: a failed check is a note in the settings
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        _logger?.LogInformation("FerretSharp shutting down");
        CloseConnection();

        if (_host is not null)
        {
            await _host.StopAsync(TimeSpan.FromSeconds(5));
            _host.Dispose();
        }

        await Log.CloseAndFlushAsync();
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
        Log.Error(e.Exception, "Unhandled exception on UI thread");
        _host?.Services.GetRequiredService<IDialogService>().ShowError(CommonText.UnexpectedError, e.Exception);
        e.Handled = true;
    }
}
