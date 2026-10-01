using System.IO;
using System.Windows;
using System.Windows.Threading;
using FerretSharp.App.Services;
using FerretSharp.App.Views;
using FerretSharp.Core;
using FerretSharp.Core.Connections;
using FerretSharp.Core.Oracle;
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

    protected override async void OnStartup(StartupEventArgs e)
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

        var builder = Host.CreateApplicationBuilder(e.Args);
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
        builder.Services.AddSingleton<ActiveConnection>();
        builder.Services.AddSingleton(WindowTheme.FromArgs(e.Args));
        builder.Services.AddSingleton<IDialogService, DialogService>();
        builder.Services.AddSingleton<MainWindow>();

        _host = builder.Build();
        _logger = _host.Services.GetRequiredService<ILogger<App>>();
        RegisterGlobalExceptionHandlers();

        await _host.StartAsync();
        _logger.LogInformation("FerretSharp {Version} started", typeof(App).Assembly.GetName().Version);

        var window = _host.Services.GetRequiredService<MainWindow>();
        MainWindow = window;
        window.Show();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        _logger?.LogInformation("FerretSharp shutting down");

        if (_host is not null)
        {
            await _host.StopAsync(TimeSpan.FromSeconds(5));
            _host.Dispose();
        }

        await Log.CloseAndFlushAsync();
        base.OnExit(e);
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
        _host?.Services.GetRequiredService<IDialogService>().ShowError("Unexpected error", e.Exception);
        e.Handled = true;
    }
}
