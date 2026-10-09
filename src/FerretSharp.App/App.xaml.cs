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
    private IHost? _host;
    private ILogger<App>? _logger;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // "--data-dir=<path>" redirects connections, workspaces and logs (tests, screenshots, separate profiles).
        var dataDir = e.Args.FirstOrDefault(a => a.StartsWith("--data-dir=", StringComparison.OrdinalIgnoreCase))?["--data-dir=".Length..];
        var paths = string.IsNullOrWhiteSpace(dataDir) ? AppPaths.Default : new AppPaths(Path.GetFullPath(dataDir));
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Information)
            .WriteTo.File(
                Path.Combine(paths.LogsDirectory, "ferretsharp-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14)
            .CreateLogger();

        // Settings decide the theme, which must be known before the window and the WebView exist (no white flash), and the
        // language. Read here, not in an async method: the UI culture belongs to the execution context, and a change made
        // inside an async method is undone when it returns – Blazor would render in the Windows language again.
        var settingsStore = new SettingsStore(paths.SettingsFile);
        var settings = AppSettings.Default;
        string? settingsError = null;
        try
        {
            settings = Task.Run(() => settingsStore.LoadAsync(CancellationToken.None)).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is SettingsStoreException or IOException or UnauthorizedAccessException)
        {
            settingsError = ex.Message;
        }

        // The UI language (WP-29, ADR 0017): only the UI culture changes, so numbers and dates keep their formats. The enum
        // display names of a linked C# project keep the Windows language (the projects may only have German resources).
        var windowsUiCulture = CultureInfo.CurrentUICulture;
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.CurrentUICulture =
            UiLanguages.Culture(UiLanguages.FromArguments(e.Args) ?? settings.Language);

        Start(e.Args, paths, settingsStore, settings, settingsError, windowsUiCulture);
    }

    private async void Start(
        string[] args, AppPaths paths, SettingsStore settingsStore, AppSettings settings, string? settingsError, CultureInfo windowsUiCulture)
    {
        var theme = WindowTheme.Create(args, settings.Theme);

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
        builder.Services.AddSingleton(new AppSettingsService(settingsStore, settings));
        builder.Services.AddSingleton<ShortcutService>();
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
        builder.Services.AddScoped<ClrModelManager>();
        builder.Services.AddScoped<PresentationService>();
        builder.Services.AddScoped<LinqConsoleService>();
        builder.Services.AddSingleton<ExitGuard>();
        builder.Services.AddSingleton<MainWindow>();
        builder.Services.AddSingleton(sp => new UiStallMonitor(
            Dispatcher, sp.GetRequiredService<ILogger<UiStallMonitor>>(), paths.LogsDirectory, sp.GetRequiredService<AppSettingsService>()));

        _host = builder.Build();
        _logger = _host.Services.GetRequiredService<ILogger<App>>();
        RegisterGlobalExceptionHandlers();

        await _host.StartAsync();
        _logger.LogInformation("FerretSharp {Version} started", typeof(App).Assembly.GetName().Version);
        if (settingsError is not null)
        {
            _logger.LogWarning("Settings could not be read, using defaults: {Error}", settingsError);
        }

        var window = _host.Services.GetRequiredService<MainWindow>();
        MainWindow = window;
        window.Show();
        _host.Services.GetRequiredService<UiStallMonitor>().Start();
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

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            _logger?.LogCritical(args.ExceptionObject as Exception, "Unhandled exception (terminating: {IsTerminating})", args.IsTerminating);

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            _logger?.LogError(args.Exception, "Unobserved task exception");
            args.SetObserved();
        };
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _logger?.LogError(e.Exception, "Unhandled exception on UI thread");
        _host?.Services.GetRequiredService<IDialogService>().ShowError(CommonText.UnexpectedError, e.Exception);
        e.Handled = true;
    }
}
