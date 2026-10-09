using FerretSharp.Core.ClrModel;
using FerretSharp.Core.Connections;
using FerretSharp.Core.Schema;
using FerretSharp.Core.Workspaces;
using NSubstitute;

namespace FerretSharp.Integration.Tests;

/// <summary>
/// ADR 0016 end to end, on a copy of the sample project: while the LINQ console runs, the project can be built, and model
/// and console pick up the new build by themselves – without reconnecting. No database needed (the schema is empty).
/// </summary>
public sealed class ModelReloadTests : IAsyncLifetime
{
    private static readonly TimeSpan Patience = TimeSpan.FromMinutes(2);

    private readonly TestFolder _folder = new();
    private string _project = null!;
    private ConnectionProfile _profile = null!;
    private ActiveConnection _active = null!;
    private ClrModelManager _models = null!;
    private LinqConsoleService _consoles = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Kunde => Path.Combine(_folder.Path, "samples", "FerretSharp.SampleModel.Entities", "Kunde.cs");

    public async ValueTask InitializeAsync()
    {
        var repository = ModelHostTests.RepositoryRoot;
        CopyProject(Path.Combine(repository, "samples"), Path.Combine(_folder.Path, "samples"));
        File.Copy(Path.Combine(repository, "nuget.config"), Path.Combine(_folder.Path, "nuget.config"));
        _project = Path.Combine(_folder.Path, "samples", "FerretSharp.SampleModel.Data", "FerretSharp.SampleModel.Data.csproj");
        var build = await BuildAsync();
        Assert.True(build.Succeeded, build.Output);

        _profile = new ConnectionProfile(Guid.NewGuid(), "Reload", ConnectionKind.Dev, new HostPortAddress("localhost", 1521, "FREEPDB1", null), "app", null, false)
        {
            ClrProject = new ClrProjectLink(_project),
        };
        var secrets = Substitute.For<ISecretStore>();
        secrets.GetPassword(_profile.Id).Returns("pw");
        var connections = new ConnectionManager(Substitute.For<IConnectionStore>(), secrets);
        var connector = Substitute.For<IDatabaseConnector>();
        var connection = Substitute.For<IDatabaseConnection>();
        var reader = Substitute.For<ISchemaReader>();
        connection.Schema.Returns(reader);
        reader.GetTablesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns([]);
        reader.GetSynonymTargetsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(SynonymTargets.None);
        reader.GetForeignKeysAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns([]);
        connector.OpenAsync(Arg.Any<ConnectionProfile>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(connection);
        var workspaces = new WorkspaceManager(new WorkspaceStore(Path.Combine(_folder.Path, "workspaces")), connections, connector);
        _active = new ActiveConnection(connections, connector, new RecentConnections(Path.Combine(_folder.Path, "recent.json")), workspaces);
        _models = new ClrModelManager(ModelHostTests.Runner(), _active, connections, buildQuiet: TimeSpan.FromMilliseconds(500));
        _consoles = new LinqConsoleService(ModelHostTests.Runner(), _models);
    }

    public async ValueTask DisposeAsync()
    {
        await _consoles.DisposeAsync();
        _models.Dispose();
        await _active.DisposeAsync();
        _folder.Dispose();
    }

    /// <summary>The project's own folders without bin and obj.</summary>
    private static void CopyProject(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        }

        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            if (Path.GetFileName(directory) is not ("bin" or "obj"))
            {
                CopyProject(directory, Path.Combine(target, Path.GetFileName(directory)));
            }
        }
    }

    private Task<DotNetRun> BuildAsync() =>
        DotNetCli.RunAsync(["build", _project, "-c", "Debug", "-nologo", "--disable-build-servers"], _folder.Path, TimeSpan.FromMinutes(5), Ct);

    private static async Task WaitUntil(Func<Task<bool>> condition, string what)
    {
        var until = DateTime.UtcNow + Patience;
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < until, $"Timed out waiting for: {what}");
            await Task.Delay(250, Ct);
        }
    }

    private bool ModelKnows(string property) =>
        _models.State is { Phase: ClrModelPhase.Loaded, Mapping: { } mapping }
        && mapping.Model.Entities.Single(e => e.ClrType == "FerretSharp.SampleModel.Entities.Kunde").Properties.Any(p => p.Name == property);

    [Fact]
    public async Task A_build_while_the_console_runs_reaches_model_and_console_without_reconnecting()
    {
        const string query = "return db.Kunden.Select(k => k.Notiz);";
        await _active.ConnectAsync(_profile, Ct);
        await WaitUntil(() => Task.FromResult(_models.State.Phase == ClrModelPhase.Loaded), "model loaded");
        Assert.False(ModelKnows("Notiz"));
        Assert.True((await _consoles.RunAsync(query, "", Ct)).HasErrors); // the console runs now and knows no Notiz

        var source = await File.ReadAllTextAsync(Kunde, Ct);
        await File.WriteAllTextAsync(Kunde, source.Replace(
            "public string? Email { get; set; }", "public string? Email { get; set; }\n\n    public string? Notiz { get; set; }", StringComparison.Ordinal), Ct);
        var build = await BuildAsync();

        Assert.True(build.Succeeded, build.Output); // before ADR 0016: "The file is locked by: .NET Host"
        await WaitUntil(() => Task.FromResult(ModelKnows("Notiz")), "model with the new property");
        await WaitUntil(async () => !(await _consoles.RunAsync(query, "", Ct)).HasErrors, "console on the new build");
        Assert.Equal(LinqConsolePhase.Ready, _consoles.State.Phase);
    }
}
