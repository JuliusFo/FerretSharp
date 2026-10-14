using FerretSharp.Core.ClrModel;
using FerretSharp.Core.Connections;
using FerretSharp.Core.Data;
using FerretSharp.Core.Query;
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

    public static readonly PlSqlObjectSummary Rechnung = new("APP_USER", "PKG_RECHNUNG", PlSqlKind.Package, "VALID", "VALID");

    private readonly TestFolder _folder = new();
    private readonly ServiceProvider _services;

    public TestApp()
    {
        var connector = FakeDatabase.Connector(_ => NewConnection());

        var services = new ServiceCollection();
        services.AddSingleton<ISecretStore>(Secrets);
        services.AddSingleton(Substitute.For<IConnectionStore>());
        services.AddSingleton<ConnectionManager>();
        services.AddSingleton(new RecentConnections(_folder.Combine("recent.json")));
        services.AddSingleton(connector);
        services.AddSingleton<IWorkspaceStore>(WorkspaceStore);
        services.AddSingleton(Substitute.For<IModelHostRunner>());
        services.AddSingleton(new AppSettingsService(new SettingsStore(_folder.Combine("settings.json")), new AppSettings()));
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
            Shell, Hub, _services.GetRequiredService<ConnectionManager>(), History = new SqlHistoryStore(_folder.Combine("history")),
            Editing, NullLogger<WorkspaceLifecycle>.Instance);
    }

    public InMemorySecretStore Secrets { get; } = new();

    public ConnectionManager Connections => _services.GetRequiredService<ConnectionManager>();

    public InMemoryWorkspaceStore WorkspaceStore { get; } = new();

    public ShellState Shell { get; } = new();

    public ConnectionHub Hub { get; }

    public WorkspaceEditing Editing { get; }

    public WorkspaceLifecycle Lifecycle { get; }

    /// <summary>The SQL history the lifecycle writes (WP-23: DDL of the designer).</summary>
    public SqlHistoryStore History { get; }

    /// <summary>A profile as the connection dialog creates it: Prod is read-only (locked workspaces) unless a test says otherwise.</summary>
    public static ConnectionProfile Profile(string name, ConnectionKind kind = ConnectionKind.Test) => new(
        Guid.NewGuid(), name, kind, new HostPortAddress("db01.example.com", 1521, "ORCLPDB", null), "app_user", null,
        ConnectionProfile.IsReadOnlyByDefault(kind));

    /// <summary>Opens and shows the connection, then aligns the shell's tabs with its workspaces (what Shell.razor does on events).</summary>
    public async Task<ConnectionScope> OpenAsync(ConnectionProfile profile)
    {
        Secrets.SetPassword(profile.Id, "pw");
        var scope = Hub.Show(profile);
        if (ConnectionHub.NeedsConnect(scope))
        {
            await scope.Active.ConnectAsync(profile, CancellationToken.None);
        }

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

    /// <summary>The structure of KUNDEN as the database reports it now (a test changes it to play DDL).</summary>
    public TableDetails KundenDetails { get; set; } = new(
        Kunden, [new ColumnInfo("ID", "NUMBER", null, false, 10, 0, false, false, null, 1)], ["ID"], [], false);

    /// <summary>Sessions with locks on any table; null = no rights on the V$ views.</summary>
    public IReadOnlyList<LockHolder>? LockHolders { get; set; }

    /// <summary>What the database lists as PL/SQL units (on connect and on a schema refresh).</summary>
    public IReadOnlyList<PlSqlObjectSummary> PlSqlObjects { get; set; } = [Rechnung];

    public static DatabaseException LostError() => new("ORA-03113: end-of-file on communication channel", "ORA-03113");

    /// <summary>DDL of any session: the error it fails with, or null to run (WP-23).</summary>
    public Func<string, Exception?>? FailDdl { get; set; }

    // Sessions open and run on pool threads (no UI dispatcher in the tests): both lists under a lock, read as a copy.
    private readonly Lock _lock = new();
    private readonly List<string> _ranDdl = [];
    private readonly List<FakeDataEditor> _editors = [];

    /// <summary>DDL that ran in any session, in order.</summary>
    public IReadOnlyList<string> RanDdl
    {
        get
        {
            lock (_lock)
            {
                return [.. _ranDdl];
            }
        }
    }

    /// <summary>The editors of the sessions opened so far (explorer and workspaces), oldest first.</summary>
    public IReadOnlyList<FakeDataEditor> Editors
    {
        get
        {
            lock (_lock)
            {
                return [.. _editors];
            }
        }
    }

    /// <summary>What a query of the SQL editor or the LINQ console reads (its first page); a test may make it fail or wait.</summary>
    public Func<QuerySpec, CancellationToken, Task<SqlPage>> ReadSql { get; set; } = (_, _) =>
        Task.FromResult(new SqlPage([new SqlColumn("ID", new ColumnInfo("ID", "NUMBER", null, false, null, null, false, false, null, 1))], [[1m]], IsLastPage: true, TimeSpan.FromMilliseconds(5)));

    /// <summary>Every session's transaction ends behind FerretSharp's back (<see cref="FakeDataEditor.Ended"/>).</summary>
    public void EndTransactions()
    {
        foreach (var editor in Editors)
        {
            editor.Ended = true;
        }
    }

    /// <summary>What a commit of any workspace session does; completes at once unless a test holds it.</summary>
    public Func<Task>? OnCommit { get; set; }

    private IDatabaseConnection NewConnection()
    {
        var reader = FakeDatabase.Reader(Kunden);
        reader.GetPlSqlObjectsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(_ => PlSqlObjects);
        reader.GetDetailsAsync(Arg.Any<TableSummary>(), Arg.Any<CancellationToken>()).Returns(_ => KundenDetails);
        reader.GetLockHoldersAsync(Arg.Any<TableRef>(), Arg.Any<CancellationToken>()).Returns(_ => LockHolders);
        var editor = new FakeDataEditor(() => OnCommit, sql =>
        {
            var failure = FailDdl?.Invoke(sql);
            if (failure is null)
            {
                lock (_lock)
                {
                    _ranDdl.Add(sql);
                }
            }

            return failure;
        });
        lock (_lock)
        {
            _editors.Add(editor);
        }

        var data = Substitute.For<IDataAccess>();
        data.ReadSqlAsync(Arg.Any<QuerySpec>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(call => ReadSql(call.Arg<QuerySpec>(), call.Arg<CancellationToken>()));
        var connection = FakeDatabase.Connection(reader);
        connection.Data.Returns(data);
        connection.Editor.Returns(editor);
        connection.UseReadOnlySnapshotsAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            editor.ReadOnly = true;
            return Task.CompletedTask;
        });
        connection.StopReadOnlySnapshotsAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            editor.ReadOnly = false;
            return Task.CompletedTask;
        });
        connection.UsesReadOnlySnapshots.Returns(_ => editor.ReadOnly);
        // Open while something is written (the fake editor's own transaction is always open, for the editing tests).
        connection.Transaction.Returns(_ => editor.Actions.Count > 0 ? editor.Transaction : TransactionInfo.None);
        return connection;
    }

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        _folder.Dispose(); // retries: a reconnect may still write recent.json in the background
    }
}
