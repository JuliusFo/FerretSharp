using FerretSharp.Core.ClrModel;
using FerretSharp.Core.Connections;
using FerretSharp.Core.Tests.ClrModel.UsageSamples;
using FerretSharp.Core.Tests.Connections;
using FerretSharp.Core.Workspaces;
using NSubstitute;

namespace FerretSharp.Core.Tests.ClrModel.Usage;

/// <summary>The index per connection: built on demand, reused while model and builds are the same, rebuilt after a build.</summary>
public sealed class CodeUsageServiceTests : IAsyncDisposable
{
    private readonly TestFolder _folder = new();
    private readonly IModelHostRunner _runner = Substitute.For<IModelHostRunner>();
    private readonly ActiveConnection _active;
    private readonly ConnectionManager _connections;
    private readonly ClrModelManager _models;
    private readonly CodeUsageService _usage;
    private readonly ConnectionProfile _profile;
    private readonly string _dll;

    public CodeUsageServiceTests()
    {
        var project = Path.Combine(Directory.CreateDirectory(_folder.Combine("Shop.Data")).FullName, "Shop.Data.csproj");
        File.WriteAllText(project, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.SetLastWriteTimeUtc(project, DateTime.UtcNow.AddHours(-2));
        var output = Directory.CreateDirectory(_folder.Combine("Shop.Data", "bin", "Debug", "net8.0"));
        _dll = Path.Combine(output.FullName, "Shop.Data.dll");
        var source = typeof(Customer).Assembly.Location;
        File.Copy(source, _dll);
        File.Copy(Path.ChangeExtension(source, ".pdb"), Path.ChangeExtension(_dll, ".pdb"));
        File.SetLastWriteTimeUtc(_dll, DateTime.UtcNow.AddHours(-1));
        File.WriteAllText(Path.Combine(output.FullName, "Shop.Data.deps.json"),
            """{ "runtimeTarget": { "name": ".NETCoreApp,Version=v8.0" }, "libraries": { "Microsoft.EntityFrameworkCore/8.0.0": {} } }""");
        File.WriteAllText(_folder.Combine("Shop.slnx"), """<Solution><Project Path="Shop.Data/Shop.Data.csproj" /></Solution>""");
        File.WriteAllText(_folder.Combine("Other.sln"), """Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Shop.Data", "Shop.Data\Shop.Data.csproj", "{22222222-2222-2222-2222-222222222222}" """);
        _profile = TestProfiles.HostPort() with { ClrProject = new ClrProjectLink(project) };

        var secrets = new InMemorySecretStore();
        secrets.SetPassword(_profile.Id, "pw");
        var store = Substitute.For<IConnectionStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns([_profile]);
        _connections = new ConnectionManager(store, secrets);
        var connector = FakeDatabase.Connector(FakeDatabase.Connection(FakeDatabase.Reader()));
        var workspaces = new WorkspaceManager(new InMemoryWorkspaceStore(), _connections, connector);
        _active = new ActiveConnection(_connections, connector, new RecentConnections(_folder.Combine("recent.json")), workspaces);
        _runner.ReadModelAsync(Arg.Any<ClrProjectLink>(), Arg.Any<BuildOutput>(), Arg.Any<CancellationToken>(), Arg.Any<IProgress<string>?>())
            .Returns(_ => new ModelHostResult(CodeUsageIndexTests.Model(), null));
        _models = new ClrModelManager(_runner, _active, _connections, buildQuiet: TimeSpan.FromHours(1));
        _usage = new CodeUsageService(_models, _active, _connections, new CodeUsageBuilder(_folder.Combine("cache")));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task ConnectAsync()
    {
        await _connections.LoadAsync(Ct);
        await _active.ConnectAsync(_profile, Ct);
        for (var waited = 0; _models.State.Phase != ClrModelPhase.Loaded; waited += 20)
        {
            Assert.True(waited < 10_000, "The model did not load.");
            await Task.Delay(20, Ct);
        }
    }

    [Fact]
    public async Task Nothing_is_built_before_a_view_asks_and_then_once_while_nothing_changed()
    {
        Assert.Null(await _usage.EnsureAsync(Ct));
        await ConnectAsync();
        Assert.Equal(CodeUsagePhase.NotBuilt, _usage.State.Phase);

        var first = await _usage.EnsureAsync(Ct);
        var again = await _usage.EnsureAsync(Ct);

        Assert.NotNull(first);
        Assert.Same(first, again);
        Assert.Equal(CodeUsagePhase.Ready, _usage.State.Phase);
        var customer = first.Index.Model.Entities.Single(e => e.Name == "Customer");
        Assert.NotEmpty(first.Index.ForProperty(customer, "Email"));
    }

    [Fact]
    public async Task A_new_build_of_an_assembly_is_read_again()
    {
        await ConnectAsync();
        var first = await _usage.EnsureAsync(Ct);

        File.SetLastWriteTimeUtc(_dll, DateTime.UtcNow);

        Assert.NotSame(first, await _usage.EnsureAsync(Ct));
    }

    [Fact]
    public async Task Of_several_solutions_the_first_is_read_until_another_is_chosen_for_the_connection()
    {
        await ConnectAsync();

        Assert.Equal([_folder.Combine("Other.sln"), _folder.Combine("Shop.slnx")], _usage.Solutions);
        Assert.Equal(_folder.Combine("Other.sln"), (await _usage.EnsureAsync(Ct))!.Solution);

        await _usage.SelectSolutionAsync(_folder.Combine("Shop.slnx"), Ct);

        Assert.Equal(_folder.Combine("Shop.slnx"), _connections.Profiles.Single().CodeSolution);
        Assert.Equal(_folder.Combine("Shop.slnx"), _usage.State.Result!.Solution);
        Assert.Same(_usage.State.Result, await _usage.EnsureAsync(Ct));
    }

    public async ValueTask DisposeAsync()
    {
        _usage.Dispose();
        _models.Dispose();
        await _active.DisposeAsync();
        _folder.Dispose();
    }
}
