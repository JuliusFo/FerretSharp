using FerretSharp.Core.ClrModel;
using FerretSharp.Core.Connections;
using FerretSharp.Core.Schema;
using FerretSharp.Core.Tests.Connections;
using FerretSharp.Core.Workspaces;
using FerretSharp.Testing;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace FerretSharp.Core.Tests.ClrModel;

/// <summary>
/// A connection linked to a model file instead of a project (WP-64): the model loads without build, cache or model host,
/// a new export copied over the file reloads it, and what cannot be read ends in "Failed" with the reason.
/// </summary>
public sealed class ModelFileLoadingTests : IAsyncDisposable
{
    private static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(200);

    private readonly TestFolder _folder = new();
    private readonly IModelHostRunner _runner = Substitute.For<IModelHostRunner>();
    private readonly FakeTimeProvider _time = new();
    private readonly ConnectionManager _connections;
    private readonly ActiveConnection _active;
    private readonly ClrModelManager _models;
    private readonly ConnectionProfile _profile;
    private readonly string _file;

    public ModelFileLoadingTests()
    {
        _file = _folder.Combine("Shop.Data.ferretmodel.json");
        _profile = TestProfiles.HostPort() with { ClrModelFile = _file };
        var secrets = new InMemorySecretStore();
        secrets.SetPassword(_profile.Id, "pw");
        _connections = new ConnectionManager(Substitute.For<IConnectionStore>(), secrets);
        var reader = FakeDatabase.Reader();
        reader.GetTablesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => (IReadOnlyList<TableSummary>)[new TableSummary(call.ArgAt<string>(0), "KUNDEN", TableKind.Table)]);
        reader.GetColumnsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(_ =>
            (IReadOnlyDictionary<string, IReadOnlyList<ColumnInfo>>)new Dictionary<string, IReadOnlyList<ColumnInfo>>
            {
                ["KUNDEN"] = [new ColumnInfo("KUNDE_ID", "NUMBER", null, false, 10, 0, false, false, null, 1)],
            });
        var connector = FakeDatabase.Connector(FakeDatabase.Connection(reader));
        var workspaces = new WorkspaceManager(new InMemoryWorkspaceStore(), _connections, connector);
        _active = new ActiveConnection(_connections, connector, new RecentConnections(_folder.Combine("recent.json")), workspaces);
        _models = new ClrModelManager(_runner, _active, _connections, buildQuiet: Quiet, time: _time);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask DisposeAsync()
    {
        _models.Dispose();
        await _active.DisposeAsync();
        _folder.Dispose();
    }

    private static ModelExport Model(params string[] properties) => new(ModelExport.CurrentFormatVersion, "8.0.11", "Shop.Ctx", "options", null,
    [
        new EntityExport("Shop.Kunde", "Shop.Kunde", false, null, "KUNDEN", null, null, null,
            properties.Select(p => new PropertyExport(p, "int", false, false, p == "KundeId" ? "KUNDE_ID" : p.ToUpperInvariant(), null, null, null, false, null)).ToList(),
            ["KundeId"], []),
    ], "de-DE");

    private static readonly ModelFileOrigin Origin = new("Shop.Data", "Release", null, DateTimeOffset.Now, "release", "a1b2c3d", "3.53.0");

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var waited = 0; !condition(); waited += 20)
        {
            Assert.True(waited < 10_000, "Timed out.");
            await Task.Delay(20, Ct);
        }
    }

    [Fact]
    public async Task A_linked_model_file_loads_without_the_model_host()
    {
        await ModelFile.WriteAsync(_file, Model("KundeId"), Origin, Ct);

        await _active.ConnectAsync(_profile, Ct);
        await WaitUntil(() => _models.State.Phase == ClrModelPhase.Loaded);

        Assert.Equal((null, _file, Origin), (_models.State.Link, _models.State.ModelFile, _models.State.Origin));
        Assert.True(_models.State.IsLinked);
        Assert.Equal(1, _models.Mapping!.MappedEntityCount);
        Assert.Null(_models.State.Output);
        await _runner.DidNotReceiveWithAnyArgs().ReadModelAsync(default!, default!, Ct, default);
        Assert.Null(await _models.BuildAsync()); // nothing to build
    }

    [Fact]
    public async Task A_missing_file_fails_with_its_path_and_loads_once_it_is_copied_there()
    {
        await _active.ConnectAsync(_profile, Ct);
        await WaitUntil(() => _models.State.Phase == ClrModelPhase.Failed);
        Assert.Equal(ClrModelErrorKind.ModelFile, _models.State.Error!.Kind);
        Assert.Contains(_file, _models.State.Error.Message);

        // The colleague's file arrives: the watcher on the folder reloads after its quiet period.
        await ModelFile.WriteAsync(_file, Model("KundeId"), Origin, Ct);
        for (var waited = 0; _models.State.Phase != ClrModelPhase.Loaded; waited += 20)
        {
            Assert.True(waited < 10_000, "The file was not reloaded.");
            await Task.Run(() => _time.Advance(Quiet), Ct);
            await Task.Delay(20, Ct);
        }

        Assert.Equal(Origin, _models.State.Origin);
    }

    [Fact]
    public async Task A_new_export_copied_over_the_file_reloads_the_model()
    {
        await ModelFile.WriteAsync(_file, Model("KundeId"), Origin, Ct);
        await _active.ConnectAsync(_profile, Ct);
        await WaitUntil(() => _models.State.Phase == ClrModelPhase.Loaded);
        Assert.DoesNotContain(_models.Mapping!.Issues, i => i.Kind == MappingIssueKind.PropertyWithoutColumn);

        var newer = Origin with { Commit = "fffffff" };
        await ModelFile.WriteAsync(_file, Model("KundeId", "Fax"), newer, Ct);
        for (var waited = 0; _models.State.Origin != newer || _models.State.Phase != ClrModelPhase.Loaded; waited += 20)
        {
            Assert.True(waited < 10_000, "The file was not reloaded.");
            await Task.Run(() => _time.Advance(Quiet), Ct);
            await Task.Delay(20, Ct);
        }

        Assert.Contains(_models.Mapping!.Issues, i => i.Kind == MappingIssueKind.PropertyWithoutColumn);
    }

    [Fact]
    public async Task A_project_s_model_exports_as_a_file_that_loads_like_the_project()
    {
        var project = Path.Combine(Directory.CreateDirectory(_folder.Combine("Shop.Data")).FullName, "Shop.Data.csproj");
        File.WriteAllText(project, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.SetLastWriteTimeUtc(project, DateTime.UtcNow.AddHours(-1));
        var output = Directory.CreateDirectory(_folder.Combine("Shop.Data", "bin", "Release", "net8.0"));
        File.WriteAllText(Path.Combine(output.FullName, "Shop.Data.dll"), "");
        File.WriteAllText(Path.Combine(output.FullName, "Shop.Data.deps.json"),
            """{ "runtimeTarget": { "name": ".NETCoreApp,Version=v8.0" }, "libraries": { "Microsoft.EntityFrameworkCore/8.0.0": {} } }""");
        _runner.ReadModelAsync(Arg.Any<ClrProjectLink>(), Arg.Any<BuildOutput>(), Arg.Any<CancellationToken>(), Arg.Any<IProgress<string>?>())
            .Returns(new ModelHostResult(Model("KundeId"), null));
        var linked = _profile with { ClrProject = new ClrProjectLink(project, "Release"), ClrModelFile = null };
        await _connections.SaveAsync(linked, null, Ct);
        await _active.ConnectAsync(linked, Ct);
        await WaitUntil(() => _models.State is { Phase: ClrModelPhase.Loaded, Link: not null });
        var exported = _folder.Combine("export", "Shop.Data.ferretmodel.json");

        await _models.ExportAsync(exported, "3.53.0", Ct);

        var read = await ModelFile.ReadAsync(exported, Ct);
        Assert.Equal(("Shop.Data", "Release", "3.53.0"), (read.Origin!.Project, read.Origin.Configuration, read.Origin.AppVersion));
        Assert.Equal(_models.Mapping!.Model.Entities[0], read.Model!.Entities[0], (a, b) => a.Name == b.Name && a.Properties.SequenceEqual(b.Properties));

        // Switching the connection to the file: the model now comes from there.
        await _connections.SaveAsync(linked with { ClrProject = null, ClrModelFile = exported }, null, Ct);
        await WaitUntil(() => _models.State is { Phase: ClrModelPhase.Loaded, ModelFile: not null });
        Assert.Null(_models.State.Link);
        Assert.Equal(read.Origin, _models.State.Origin);
    }

    [Fact]
    public async Task Exporting_needs_a_project_s_model()
    {
        await ModelFile.WriteAsync(_file, Model("KundeId"), Origin, Ct);
        await _active.ConnectAsync(_profile, Ct);
        await WaitUntil(() => _models.State.Phase == ClrModelPhase.Loaded);

        await Assert.ThrowsAsync<InvalidOperationException>(() => _models.ExportAsync(_folder.Combine("weiter.json"), "3.53.0", Ct));
    }
}
