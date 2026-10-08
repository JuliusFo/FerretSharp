using FerretSharp.Core.ClrModel;
using FerretSharp.Core.Connections;
using FerretSharp.Core.Schema;
using FerretSharp.Core.Tests.Connections;
using FerretSharp.Core.Tests.Fakes;
using FerretSharp.Core.Workspaces;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace FerretSharp.Core.Tests.ClrModel;

/// <summary>
/// Model and LINQ console load in the background: whatever goes wrong must end in "Failed" with a message – never leave
/// the status bar at "Lädt …" (a deps.json rewritten by a build running right now, a model host that cannot start). A new
/// build is picked up by itself (ADR 0016), the previous model and console stay usable until the new ones are ready.
/// </summary>
public sealed class ClrModelLoadingTests : IAsyncDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "fs-model-" + Guid.NewGuid().ToString("N")));
    private readonly IModelHostRunner _runner = Substitute.For<IModelHostRunner>();
    private readonly ActiveConnection _active;
    private readonly ConnectionManager _connections;
    private ClrModelManager _models;
    private readonly string _deps;
    private readonly string _dll;
    private readonly ConnectionProfile _profile;

    public ClrModelLoadingTests()
    {
        var project = Path.Combine(Directory.CreateDirectory(Path.Combine(_root.FullName, "Shop.Data")).FullName, "Shop.Data.csproj");
        File.WriteAllText(project, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.SetLastWriteTimeUtc(project, DateTime.UtcNow.AddHours(-1));
        var output = Directory.CreateDirectory(Path.Combine(_root.FullName, "Shop.Data", "bin", "Debug", "net8.0"));
        _dll = Path.Combine(output.FullName, "Shop.Data.dll");
        File.WriteAllText(_dll, "");
        _deps = Path.Combine(output.FullName, "Shop.Data.deps.json");
        File.WriteAllText(_deps, """{ "runtimeTarget": { "name": ".NETCoreApp,Version=v8.0" } }""");
        _profile = TestProfiles.HostPort() with { ClrProject = new ClrProjectLink(project) };

        var secrets = new InMemorySecretStore();
        secrets.SetPassword(_profile.Id, "pw");
        var connections = _connections = new ConnectionManager(Substitute.For<IConnectionStore>(), secrets);
        var connector = Substitute.For<IDatabaseConnector>();
        var connection = Substitute.For<IDatabaseConnection>();
        var reader = Substitute.For<ISchemaReader>();
        connection.Schema.Returns(reader);
        reader.GetTablesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns([]);
        reader.GetSynonymTargetsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns([]);
        reader.GetForeignKeysAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns([]);
        connector.OpenAsync(Arg.Any<ConnectionProfile>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(connection);
        var workspaces = new WorkspaceManager(new InMemoryWorkspaceStore(), connections, connector);
        _active = new ActiveConnection(connections, connector, new RecentConnections(Path.Combine(_root.FullName, "recent.json")), workspaces);
        // No reloads by the watcher here (a test writing a broken deps.json would race with them); see WatchBuildsQuickly.
        _models = new ClrModelManager(_runner, _active, connections, buildQuiet: TimeSpan.FromHours(1));
    }

    /// <summary>For the tests of the watcher: reload 200 ms after the build output stopped changing.</summary>
    private void WatchBuildsQuickly()
    {
        _models.Dispose();
        _models = new ClrModelManager(_runner, _active, _connections, buildQuiet: TimeSpan.FromMilliseconds(200));
    }

    private static ModelHostResult Model() => new(new ModelExport(ModelExport.CurrentFormatVersion, "8.0.0", "Shop.Ctx", "options", null, []), null);

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var waited = 0; !condition(); waited += 20)
        {
            Assert.True(waited < 10_000, "Timed out.");
            await Task.Delay(20, Ct);
        }
    }

    private ILinqConsole Console(string name)
    {
        var console = Substitute.For<ILinqConsole>();
        console.IsAlive.Returns(true);
        console.Output.Returns(BuildOutputLocator.Find(_profile.ClrProject!));
        console.RunAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new LinqRunResult([], [], [], [], name, null, TimeSpan.Zero));
        return console;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask DisposeAsync()
    {
        _models.Dispose();
        await _active.DisposeAsync();
        _root.Delete(recursive: true);
    }

    [Fact]
    public async Task A_deps_json_that_cannot_be_read_fails_the_model_load()
    {
        await _active.ConnectAsync(_profile, Ct);
        await WaitUntil(() => _models.State.Phase is ClrModelPhase.Loaded or ClrModelPhase.Failed); // the load on connecting reads it too
        File.WriteAllText(_deps, "{ halb geschrieben");

        await _models.LoadAsync();

        Assert.Equal(ClrModelPhase.Failed, _models.State.Phase);
        Assert.NotNull(_models.State.Error);
    }

    [Fact]
    public async Task An_unexpected_runner_failure_fails_the_model_load()
    {
        await _active.ConnectAsync(_profile, Ct);
        _runner.ReadModelAsync(Arg.Any<ClrProjectLink>(), Arg.Any<BuildOutput>(), Arg.Any<CancellationToken>(), Arg.Any<IProgress<string>?>())
            .ThrowsAsync(new InvalidOperationException("kaputt"));

        await _models.LoadAsync();

        Assert.Equal(ClrModelPhase.Failed, _models.State.Phase);
        Assert.Contains("kaputt", _models.State.Error!.Message);
    }

    [Fact]
    public async Task A_console_that_cannot_start_fails_with_a_message()
    {
        await _active.ConnectAsync(_profile, Ct);
        await _models.LoadAsync();
        using var consoles = new LinqConsoleService(_runner, _models);
        _runner.StartConsoleAsync(Arg.Any<ClrProjectLink>(), Arg.Any<BuildOutput>(), Arg.Any<CancellationToken>(), Arg.Any<IProgress<string>?>())
            .ThrowsAsync(new System.ComponentModel.Win32Exception("dotnet fehlt"));

        var error = await Assert.ThrowsAsync<ClrModelException>(() => consoles.RunAsync("1", "", Ct));
        await consoles.WarmUpAsync(); // fire and forget: must not throw either

        Assert.Contains("dotnet fehlt", error.Message);
        Assert.Equal(LinqConsolePhase.Failed, consoles.State.Phase);
    }

    [Fact]
    public async Task A_new_build_reloads_the_model_and_the_old_one_stays_until_the_new_one_is_ready()
    {
        WatchBuildsQuickly();
        var reads = 0;
        var second = new TaskCompletionSource<ModelHostResult>();
        _runner.ReadModelAsync(Arg.Any<ClrProjectLink>(), Arg.Any<BuildOutput>(), Arg.Any<CancellationToken>(), Arg.Any<IProgress<string>?>())
            .Returns(_ => Interlocked.Increment(ref reads) == 1 ? Task.FromResult(Model()) : second.Task);
        var changed = 0;
        _models.BuildOutputChanged += () => Interlocked.Increment(ref changed);
        await _active.ConnectAsync(_profile, Ct);
        await WaitUntil(() => _models.State.Phase == ClrModelPhase.Loaded);
        var first = _models.Mapping;

        await File.WriteAllTextAsync(_dll, "neuer Build", Ct);

        await WaitUntil(() => _models.State is { Phase: ClrModelPhase.Loading, BuildChanged: true });
        Assert.Same(first, _models.State.Mapping);
        second.SetResult(Model());
        await WaitUntil(() => _models.State.Phase == ClrModelPhase.Loaded);
        Assert.NotSame(first, _models.Mapping);
        Assert.False(_models.State.BuildChanged);
        Assert.Equal(1, changed);
    }

    [Fact]
    public async Task Touching_the_build_output_without_changing_it_does_not_reload()
    {
        WatchBuildsQuickly();
        _runner.ReadModelAsync(Arg.Any<ClrProjectLink>(), Arg.Any<BuildOutput>(), Arg.Any<CancellationToken>(), Arg.Any<IProgress<string>?>())
            .Returns(Model());
        var changed = 0;
        _models.BuildOutputChanged += () => Interlocked.Increment(ref changed);
        await _active.ConnectAsync(_profile, Ct);
        await WaitUntil(() => _models.State.Phase == ClrModelPhase.Loaded);

        var written = File.GetLastWriteTimeUtc(_dll);
        await File.WriteAllTextAsync(_dll, "", Ct);
        File.SetLastWriteTimeUtc(_dll, written);
        await Task.Delay(800, Ct);

        Assert.Equal(0, changed);
        await _runner.Received(1).ReadModelAsync(Arg.Any<ClrProjectLink>(), Arg.Any<BuildOutput>(), Arg.Any<CancellationToken>(), Arg.Any<IProgress<string>?>());
    }

    [Fact]
    public async Task After_a_build_the_console_restarts_in_the_background_and_the_old_one_answers_meanwhile()
    {
        WatchBuildsQuickly();
        _runner.ReadModelAsync(Arg.Any<ClrProjectLink>(), Arg.Any<BuildOutput>(), Arg.Any<CancellationToken>(), Arg.Any<IProgress<string>?>())
            .Returns(Model());
        await _active.ConnectAsync(_profile, Ct);
        await WaitUntil(() => _models.State.Phase == ClrModelPhase.Loaded);
        await using var consoles = new LinqConsoleService(_runner, _models);
        var old = Console("alt");
        var started = new TaskCompletionSource<ILinqConsole>();
        _runner.StartConsoleAsync(Arg.Any<ClrProjectLink>(), Arg.Any<BuildOutput>(), Arg.Any<CancellationToken>(), Arg.Any<IProgress<string>?>())
            .Returns(Task.FromResult(old), started.Task);
        await consoles.WarmUpAsync();
        Assert.Equal(LinqConsolePhase.Ready, consoles.State.Phase);

        await File.WriteAllTextAsync(_dll, "neuer Build", Ct);

        await WaitUntil(() => consoles.State.IsReloading);
        Assert.Equal("alt", (await consoles.RunAsync("x", "", Ct)).ResultType);
        var fresh = Console("neu");
        started.SetResult(fresh);
        await WaitUntil(() => consoles.State is { Phase: LinqConsolePhase.Ready, Step: null });
        Assert.Equal("neu", (await consoles.RunAsync("x", "", Ct)).ResultType);
        await WaitUntil(() => old.ReceivedCalls().Any(c => c.GetMethodInfo().Name == nameof(ILinqConsole.DisposeAsync)));
    }

    [Fact]
    public async Task A_new_build_that_cannot_be_loaded_keeps_the_old_console()
    {
        WatchBuildsQuickly();
        _runner.ReadModelAsync(Arg.Any<ClrProjectLink>(), Arg.Any<BuildOutput>(), Arg.Any<CancellationToken>(), Arg.Any<IProgress<string>?>())
            .Returns(Model());
        await _active.ConnectAsync(_profile, Ct);
        await WaitUntil(() => _models.State.Phase == ClrModelPhase.Loaded);
        await using var consoles = new LinqConsoleService(_runner, _models);
        var old = Console("alt");
        _runner.StartConsoleAsync(Arg.Any<ClrProjectLink>(), Arg.Any<BuildOutput>(), Arg.Any<CancellationToken>(), Arg.Any<IProgress<string>?>())
            .Returns(Task.FromResult(old), Task.FromException<ILinqConsole>(new ClrModelException(ClrModelErrorKind.HostFailed, "OnModelCreating kaputt")));
        await consoles.WarmUpAsync();

        await File.WriteAllTextAsync(_dll, "kaputter Build", Ct);

        await WaitUntil(() => consoles.State.Error is not null);
        Assert.Equal(LinqConsolePhase.Ready, consoles.State.Phase);
        Assert.Contains("OnModelCreating kaputt", consoles.State.Error!.Message);
        Assert.Equal("alt", (await consoles.RunAsync("x", "", Ct)).ResultType);
        // Not tried again on every run while the output is the same.
        await _runner.Received(2).StartConsoleAsync(Arg.Any<ClrProjectLink>(), Arg.Any<BuildOutput>(), Arg.Any<CancellationToken>(), Arg.Any<IProgress<string>?>());
    }

    [Fact]
    public async Task A_console_start_can_be_cancelled()
    {
        await _active.ConnectAsync(_profile, Ct);
        await _models.LoadAsync();
        await using var consoles = new LinqConsoleService(_runner, _models);
        var hanging = new TaskCompletionSource<ILinqConsole>();
        _runner.StartConsoleAsync(Arg.Any<ClrProjectLink>(), Arg.Any<BuildOutput>(), Arg.Any<CancellationToken>(), Arg.Any<IProgress<string>?>())
            .Returns(call =>
            {
                call.Arg<CancellationToken>().Register(() => hanging.TrySetCanceled());
                return hanging.Task;
            });

        var warmUp = consoles.WarmUpAsync();
        await WaitUntil(() => consoles.State.Phase == LinqConsolePhase.Starting);
        consoles.CancelStart();
        await warmUp;

        Assert.Equal(LinqConsolePhase.Stopped, consoles.State.Phase);
    }
}
