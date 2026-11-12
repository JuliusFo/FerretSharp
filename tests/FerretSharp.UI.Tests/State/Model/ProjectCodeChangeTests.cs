using FerretSharp.Core.ClrModel;
using FerretSharp.Core.Design;
using FerretSharp.Core.Schema;
using FerretSharp.UI.State;
using NSubstitute;

namespace FerretSharp.UI.Tests.State;

/// <summary>WP-41: a designer change into the work clone – only one that holds the linked project, written once.</summary>
public sealed class ProjectCodeChangeTests : IDisposable
{
    private const string Owner = "APP";

    private readonly TestFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    private static readonly TableDetails Kunden = new(new TableSummary(Owner, "KUNDEN", TableKind.Table),
        [
            new("KUNDE_ID", "NUMBER", null, false, 10, 0, false, false, null, 1),
            new("EMAIL", "VARCHAR2", 200, true, null, null, true, false, null, 2),
        ],
        ["KUNDE_ID"], [], false);

    private static readonly ModelExport Model = new(ModelExport.CurrentFormatVersion, "8.0.0", "Shop.Ctx", "options", null,
    [
        new EntityExport("Kunde", "Shop.Kunde", false, null, "KUNDEN", null, null, null,
            [new("KundeId", "int", false, false, "KUNDE_ID", null, null, null, false, null), new("Email", "string", true, false, "EMAIL", null, null, null, false, null)],
            ["KundeId"], [], "Kunden"),
    ]);

    private static async Task<ClrModelMapping> MappingAsync()
    {
        var reader = Substitute.For<ISchemaReader>();
        reader.GetTablesAsync(Owner, Arg.Any<CancellationToken>()).Returns((IReadOnlyList<TableSummary>)[Kunden.Table]);
        reader.GetSynonymTargetsAsync(Owner, Arg.Any<CancellationToken>()).Returns(SynonymTargets.None);
        reader.GetForeignKeysAsync(Arg.Is<IReadOnlyList<string>>(o => o.Contains(Owner)), Arg.Any<CancellationToken>()).Returns((IReadOnlyList<ForeignKeyInfo>)[]);
        reader.GetColumnsAsync(Owner, Arg.Any<CancellationToken>()).Returns((IReadOnlyDictionary<string, IReadOnlyList<ColumnInfo>>)
            new Dictionary<string, IReadOnlyList<ColumnInfo>> { ["KUNDEN"] = Kunden.Columns });
        var schema = new SchemaCache(reader, Owner);
        await schema.LoadAsync(TestContext.Current.CancellationToken);
        return await ClrModelMapping.BuildAsync(Model, schema, TestContext.Current.CancellationToken);
    }

    /// <summary>A clone with a Git HEAD and the project at src/Shop.Data, its entity class beside it.</summary>
    private string Clone(string name, bool withProject = true)
    {
        var root = Directory.CreateDirectory(_folder.Combine(name)).FullName;
        Directory.CreateDirectory(Path.Combine(root, ".git"));
        File.WriteAllText(Path.Combine(root, ".git", "HEAD"), "ref: refs/heads/feature/x");
        if (withProject)
        {
            Directory.CreateDirectory(Path.Combine(root, "src", "Shop.Data"));
            File.WriteAllText(Path.Combine(root, "src", "Shop.Data", "Shop.Data.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            File.WriteAllText(Path.Combine(root, "src", "Shop.Data", "Kunde.cs"),
                "namespace Shop;\n\npublic class Kunde\n{\n    public int KundeId { get; set; }\n    public string? Email { get; set; }\n}\n");
        }

        return root;
    }

    private static CodeChangeRequest DropEmail()
    {
        var draft = TableDraft.Of(Kunden);
        return new CodeChangeRequest.ForTable(Kunden, draft with { Columns = [.. draft.Columns.Select(c => c.Original == "EMAIL" ? c with { Dropped = true } : c)] });
    }

    [Fact]
    public async Task Without_a_work_clone_or_with_one_that_lacks_the_linked_project_nothing_is_planned()
    {
        var mapping = await MappingAsync();
        var link = new ClrProjectLink(Path.Combine(Clone("linked"), "src", "Shop.Data", "Shop.Data.csproj"));

        var none = new ProjectCodeChange(mapping, link, null, [], DropEmail());
        await none.PlanAsync(TestContext.Current.CancellationToken);
        var other = new ProjectCodeChange(mapping, link, Clone("other", withProject: false), [], DropEmail());
        await other.PlanAsync(TestContext.Current.CancellationToken);

        Assert.Equal(CodeChangeStage.NoWorkClone, none.Stage);
        Assert.Equal((CodeChangeStage.WorkCloneDoesNotFit, WorkCloneFit.ProjectMissing), (other.Stage, other.Fit));
        Assert.Null(other.Plan);
        Assert.False(other.CanWrite);
    }

    [Fact]
    public async Task A_planned_change_is_written_into_the_work_clone_once_never_into_the_linked_clone()
    {
        var mapping = await MappingAsync();
        var linked = Clone("linked");
        var link = new ClrProjectLink(Path.Combine(linked, "src", "Shop.Data", "Shop.Data.csproj"));
        var work = Clone("work");
        var change = new ProjectCodeChange(mapping, link, work, [link.ProjectFile], DropEmail());

        await change.PlanAsync(TestContext.Current.CancellationToken);
        Assert.True(change.CanWrite, string.Join("\n", change.Plan?.Problems ?? []));
        await change.WriteAsync(TestContext.Current.CancellationToken);
        await change.WriteAsync(TestContext.Current.CancellationToken); // a second click does nothing

        Assert.Equal(CodeChangeStage.Written, change.Stage);
        Assert.Equal([FileWriteOutcome.Written], change.Results!.Select(r => r.Outcome));
        Assert.Equal("namespace Shop;\n\npublic class Kunde\n{\n    public int KundeId { get; set; }\n}\n", File.ReadAllText(Path.Combine(work, "src", "Shop.Data", "Kunde.cs")));
        Assert.Contains("Email", File.ReadAllText(Path.Combine(linked, "src", "Shop.Data", "Kunde.cs")), StringComparison.Ordinal);
        Assert.False(change.CanWrite);
    }
}
