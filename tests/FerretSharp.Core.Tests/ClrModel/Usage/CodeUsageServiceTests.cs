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
        // The model's Changed reaches the service after the state is set: wait for both.
        for (var waited = 0; _models.State.Phase != ClrModelPhase.Loaded || _usage.State.Phase == CodeUsagePhase.None; waited += 20)
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

        Assert.Equal(_folder.Combine("Other.sln"), (await _usage.EnsureAsync(Ct))!.Solution);
        Assert.Equal([_folder.Combine("Other.sln"), _folder.Combine("Shop.slnx")], _usage.Solutions);

        await _usage.SelectSolutionAsync(_folder.Combine("Shop.slnx"), Ct);

        Assert.Equal(_folder.Combine("Shop.slnx"), _connections.Profiles.Single().CodeSolution);
        Assert.Equal(_folder.Combine("Shop.slnx"), _usage.State.Result!.Solution);
        Assert.Same(_usage.State.Result, await _usage.EnsureAsync(Ct));
    }

    /// <summary>A builder that waits for the test, then builds or throws (the failure paths of the service).</summary>
    private sealed class GatedBuilder(string cache, Exception? failure = null) : CodeUsageBuilder(cache)
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async Task<CodeUsageResult> BuildAsync(ClrProjectLink link, string? solution, ModelExport model, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return failure is null ? await base.BuildAsync(link, solution, model, cancellationToken) : throw failure;
        }
    }

    [Fact]
    public async Task An_unexpected_failure_ends_in_failed_not_in_building_for_ever()
    {
        await ConnectAsync();
        var builder = new GatedBuilder(_folder.Combine("cache2"), new InvalidOperationException("odd metadata"));
        builder.Release.SetResult();
        using var usage = new CodeUsageService(_models, _active, _connections, builder);

        Assert.Null(await usage.EnsureAsync(Ct)); // no fault for the views waiting for it

        Assert.Equal(CodeUsagePhase.Failed, usage.State.Phase);
        Assert.Equal("odd metadata", usage.State.Error);
    }

    [Fact]
    public async Task A_model_loaded_while_the_index_is_built_leaves_it_out_of_date()
    {
        await ConnectAsync();
        var builder = new GatedBuilder(_folder.Combine("cache2"));
        using var usage = new CodeUsageService(_models, _active, _connections, builder);
        var building = usage.EnsureAsync(Ct);
        await builder.Started.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        var before = _models.Mapping!.Model;
        await _models.LoadAsync(); // a build of the project: a new model while the index is read
        Assert.NotSame(before, _models.Mapping!.Model);
        builder.Release.SetResult();
        var result = await building;

        Assert.Same(before, result!.Index.Model);
        Assert.Equal(CodeUsagePhase.NotBuilt, usage.State.Phase); // views showing it build it again
        Assert.Same(_models.Mapping!.Model, (await usage.EnsureAsync(Ct))!.Index.Model);
    }

    /// <summary>
    /// Review 3.44.0, A4: choosing another solution while the first build ran returned that build – the view listed the new
    /// solution and showed the uses of the old one, as Ready.
    /// </summary>
    [Fact]
    public async Task A_solution_chosen_while_the_index_is_built_is_read_after_it()
    {
        await ConnectAsync();
        var builder = new GatedBuilder(_folder.Combine("cache2"));
        using var usage = new CodeUsageService(_models, _active, _connections, builder);
        var building = usage.EnsureAsync(Ct);
        await builder.Started.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        var choosing = usage.SelectSolutionAsync(_folder.Combine("Shop.slnx"), Ct);
        builder.Release.SetResult();

        Assert.Equal(_folder.Combine("Other.sln"), (await building)!.Solution);
        await choosing;
        Assert.Equal(_folder.Combine("Shop.slnx"), usage.State.Result!.Solution);
        Assert.Equal(CodeUsagePhase.Ready, usage.State.Phase);
    }

    /// <summary>"Build projects" builds the solution read, in the link's configuration, and reads what it wrote – also after a failed build.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task Building_the_solution_reads_the_new_builds_afterwards(int exitCode)
    {
        await ConnectAsync();
        var first = await _usage.EnsureAsync(Ct);
        _runner.BuildSolutionAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            Assert.True(_usage.IsBuildingSolution);
            Assert.True(_models.IsBuilding); // no build of the linked project meanwhile
            File.SetLastWriteTimeUtc(_dll, DateTime.UtcNow);
            return new DotNetRun(exitCode, "output");
        });

        var run = await _usage.BuildSolutionAsync(Ct);

        await _runner.Received(1).BuildSolutionAsync(_folder.Combine("Other.sln"), "Debug", Arg.Any<CancellationToken>());
        Assert.Equal(exitCode, run!.ExitCode);
        Assert.Same(run, _usage.LastSolutionBuild);
        Assert.Null(_models.LastBuild); // the project's build result stays the project's
        Assert.False(_usage.IsBuildingSolution);
        Assert.NotSame(first, _usage.State.Result);
        Assert.Equal(CodeUsagePhase.Ready, _usage.State.Phase);
    }

    [Fact]
    public async Task The_solutions_are_known_after_the_first_build_and_reading_them_reads_nothing()
    {
        await ConnectAsync();
        Assert.Empty(_usage.Solutions);

        await _usage.EnsureAsync(Ct);

        Assert.Equal(2, _usage.Solutions.Count);
    }

    public async ValueTask DisposeAsync()
    {
        _usage.Dispose();
        _models.Dispose();
        await _active.DisposeAsync();
        _folder.Dispose();
    }
}
