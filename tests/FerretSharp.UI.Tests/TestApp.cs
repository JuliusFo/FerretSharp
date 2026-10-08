using FerretSharp.Core.ClrModel;
using FerretSharp.Core.Connections;
using FerretSharp.Core.Data;
using FerretSharp.Core.Schema;
using FerretSharp.Core.Settings;
using FerretSharp.Core.Tests.Fakes;
using FerretSharp.Core.Workspaces;
using FerretSharp.UI.State;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace FerretSharp.UI.Tests;

/// <summary>
/// The shell's state classes wired like the app does (one DI scope per open connection, WP-24), with an in-memory
/// workspace store and a database that answers with one table. Lets tests open several connections at once.
/// </summary>
internal sealed class TestApp : IAsyncDisposable
{
    public static readonly TableSummary Kunden = new("APP_USER", "KUNDEN", TableKind.Table);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ferret-ui-tests", Guid.NewGuid().ToString("N"));
    private readonly ServiceProvider _services;

    public TestApp()
    {
        var connector = Substitute.For<IDatabaseConnector>();
        connector.OpenAsync(Arg.Any<ConnectionProfile>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(NewConnection()));

        var services = new ServiceCollection();
        services.AddSingleton<ISecretStore>(Secrets);
        services.AddSingleton(Substitute.For<IConnectionStore>());
        services.AddSingleton<ConnectionManager>();
        services.AddSingleton(new RecentConnections(Path.Combine(_directory, "recent.json")));
        services.AddSingleton(connector);
        services.AddSingleton<IWorkspaceStore>(WorkspaceStore);
        services.AddSingleton(Substitute.For<IModelHostRunner>());
        services.AddSingleton(new AppSettingsService(new SettingsStore(Path.Combine(_directory, "settings.json")), new AppSettings()));
        services.AddScoped<WorkspaceManager>();
        services.AddScoped<ActiveConnection>();
        services.AddScoped<ClrModelManager>();
        services.AddScoped<PresentationService>();
        services.AddScoped<LinqConsoleService>();
        services.AddSingleton<ConnectionHub>();
        _services = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

        Hub = _services.GetRequiredService<ConnectionHub>();
        Editing = new WorkspaceEditing(Shell, Hub, _services.GetRequiredService<AppSettingsService>(), NullLogger<WorkspaceEditing>.Instance);
        Lifecycle = new WorkspaceLifecycle(
            Shell, Hub, _services.GetRequiredService<ConnectionManager>(), new SqlHistoryStore(Path.Combine(_directory, "history")),
            Editing, NullLogger<WorkspaceLifecycle>.Instance);
    }

    public InMemorySecretStore Secrets { get; } = new();

    public InMemoryWorkspaceStore WorkspaceStore { get; } = new();

    public ShellState Shell { get; } = new();

    public ConnectionHub Hub { get; }

    public WorkspaceEditing Editing { get; }

    public WorkspaceLifecycle Lifecycle { get; }

    public static ConnectionProfile Profile(string name, ConnectionKind kind = ConnectionKind.Test) => new(
        Guid.NewGuid(), name, kind, new HostPortAddress("db01.example.com", 1521, "ORCLPDB", null), "app_user", null, false);

    /// <summary>Opens and shows the connection, then aligns the shell's tabs with its workspaces (what Shell.razor does on events).</summary>
    public async Task<ConnectionScope> OpenAsync(ConnectionProfile profile)
    {
        Secrets.SetPassword(profile.Id, "pw");
        await Hub.OpenAsync(profile);
        var scope = Hub.Find(profile.Id)!;
        Assert.True(scope.Active.IsConnected, scope.Active.Error?.Message);
        Shell.SyncWorkspaces(scope.Id, scope.Workspaces.Open, scope.Workspaces.Active?.Id, scope.Active.Schema!);
        Shell.ShowConnection(Hub.Current?.Id);
        return scope;
    }

    /// <summary>The tabs of the connection's active workspace in the shell.</summary>
    public WorkspaceTabs WorkspaceOf(ConnectionScope scope) =>
        Shell.AllWorkspaces.Single(w => w.WorkspaceId == scope.Workspaces.Active!.Id);

    /// <summary>A KUNDEN tab with one pending new row in the connection's active workspace: uncommitted work.</summary>
    public TableTab AddPendingWork(ConnectionScope scope)
    {
        var workspace = WorkspaceOf(scope);
        var tab = new TableTab(workspace.WorkspaceId, Kunden)
        {
            Changes = new ChangeTracker(new TableDetails(
                Kunden, [new ColumnInfo("ID", "NUMBER", null, false, 10, 0, false, false, null, 1)], ["ID"], [], false)),
        };
        tab.Changes.AddRow();
        workspace.Tabs.Add(tab);
        return tab;
    }

    public static DatabaseException LostError() => new("ORA-03113: end-of-file on communication channel", "ORA-03113");

    /// <summary>What a commit of any workspace session does; completes at once unless a test holds it.</summary>
    public Func<Task>? OnCommit { get; set; }

    private IDatabaseConnection NewConnection()
    {
        var reader = Substitute.For<ISchemaReader>();
        reader.GetTablesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns([Kunden]);
        reader.GetSynonymTargetsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns([]);
        reader.GetForeignKeysAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns([]);
        var editor = Substitute.For<IDataEditor>();
        editor.Transaction.Returns(new TransactionInfo(TransactionMode.ReadWrite, DateTimeOffset.Now));
        editor.Actions.Returns([]);
        editor.CommitAsync(Arg.Any<CancellationToken>()).Returns(_ => OnCommit?.Invoke() ?? Task.CompletedTask);
        var connection = Substitute.For<IDatabaseConnection>();
        connection.Schema.Returns(reader);
        connection.Editor.Returns(editor);
        connection.ServerVersion.Returns("23.26.0.0.0");
        return connection;
    }

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();

        // A reconnect runs in the background (Task.Run) and may still write recent.json while we delete: retry, then
        // leave the temp folder behind rather than failing the test (seen on Linux CI after 3.15.0).
        for (var attempt = 1; Directory.Exists(_directory); attempt++)
        {
            try
            {
                Directory.Delete(_directory, recursive: true);
            }
            catch (IOException) when (attempt < 5)
            {
                await Task.Delay(50);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return;
            }
        }
    }
}
