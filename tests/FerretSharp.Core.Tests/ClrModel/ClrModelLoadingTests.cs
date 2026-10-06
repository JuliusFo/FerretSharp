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
/// the status bar at "Lädt …" (a deps.json rewritten by a build running right now, a model host that cannot start).
/// </summary>
public sealed class ClrModelLoadingTests : IAsyncDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "fs-model-" + Guid.NewGuid().ToString("N")));
    private readonly IModelHostRunner _runner = Substitute.For<IModelHostRunner>();
    private readonly ActiveConnection _active;
    private readonly ClrModelManager _models;
    private readonly string _deps;
    private readonly ConnectionProfile _profile;

    public ClrModelLoadingTests()
    {
        var project = Path.Combine(Directory.CreateDirectory(Path.Combine(_root.FullName, "Shop.Data")).FullName, "Shop.Data.csproj");
        File.WriteAllText(project, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.SetLastWriteTimeUtc(project, DateTime.UtcNow.AddHours(-1));
        var output = Directory.CreateDirectory(Path.Combine(_root.FullName, "Shop.Data", "bin", "Debug", "net8.0"));
        File.WriteAllText(Path.Combine(output.FullName, "Shop.Data.dll"), "");
        _deps = Path.Combine(output.FullName, "Shop.Data.deps.json");
        File.WriteAllText(_deps, """{ "runtimeTarget": { "name": ".NETCoreApp,Version=v8.0" } }""");
        _profile = TestProfiles.HostPort() with { ClrProject = new ClrProjectLink(project) };

        var secrets = new InMemorySecretStore();
        secrets.SetPassword(_profile.Id, "pw");
        var connections = new ConnectionManager(Substitute.For<IConnectionStore>(), secrets);
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
        _models = new ClrModelManager(_runner, _active, connections);
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
}
