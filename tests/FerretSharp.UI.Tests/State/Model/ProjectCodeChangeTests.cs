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
        return CodeChangeRequest.ForTable.OfDraft(Kunden, draft with { Columns = [.. draft.Columns.Select(c => c.Original == "EMAIL" ? c with { Dropped = true } : c)] });
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

    /// <summary>
    /// Review WP-41, M6: after a run that stopped halfway the tab kept nothing, a second run dropped the first one not written.
    /// Now the table before the first run not written, compared with the database's columns when shown; renames chained.
    /// </summary>
    [Fact]
    public void Runs_before_writing_come_together_against_the_tables_columns_and_a_written_one_starts_anew()
    {
        var first = TableDraft.Of(Kunden) with
        {
            Columns = [.. TableDraft.Of(Kunden).Columns.Select(c => c.Original == "EMAIL" ? c with { Column = c.Column with { Name = "MAIL" } } : c),
                new ColumnDraft(null, new ColumnInfo("RABATT", "NUMBER", null, false, 5, 2, true, false, null, 3))],
        };
        var afterFirst = Kunden with
        {
            Columns = [Kunden.Columns[0], Kunden.Columns[1] with { Name = "MAIL" }, new ColumnInfo("RABATT", "NUMBER", null, false, 5, 2, true, false, null, 3)],
        };
        var second = TableDraft.Of(afterFirst) with
        {
            Columns = [.. TableDraft.Of(afterFirst).Columns.Select(c => c.Original == "MAIL" ? c with { Column = c.Column with { Name = "E_MAIL", Length = 300 } } : c)],
        };
        // The second run stopped after the rename: the length did not change.
        IReadOnlyList<ColumnInfo> database = [afterFirst.Columns[0], afterFirst.Columns[1] with { Name = "E_MAIL" }, afterFirst.Columns[2]];

        var kept = CodeChangeRequest.ForTable.AfterRun(CodeChangeRequest.ForTable.AfterRun(null, Kunden, first), afterFirst, second);
        var changes = (kept with { After = database }).Changes;

        Assert.Same(Kunden, kept.Before);
        Assert.Equal([(ColumnChangeKind.Renamed, "EMAIL", "E_MAIL"), (ColumnChangeKind.Added, "RABATT", "RABATT")], changes.Select(c => (c.Kind, c.Column, c.After!.Name)));
        Assert.Same(afterFirst, CodeChangeRequest.ForTable.AfterRun(kept with { Written = true }, afterFirst, second).Before);
    }

    /// <summary>Review WP-41, M1: the linked clone chosen as work clone was written into – it fits its own project.</summary>
    [Fact]
    public async Task The_linked_clone_of_a_connection_as_work_clone_is_refused_also_when_another_connection_links_it()
    {
        var mapping = await MappingAsync();
        var linked = Clone("linked");
        var otherConnection = Path.Combine(linked, "src", "Shop.Data", "Shop.Data.csproj");
        var link = new ClrProjectLink(Path.Combine(Clone("own"), "src", "Shop.Data", "Shop.Data.csproj"));

        var itself = new ProjectCodeChange(mapping, new ClrProjectLink(otherConnection), linked, [], DropEmail());
        await itself.PlanAsync(TestContext.Current.CancellationToken);
        var ofAnother = new ProjectCodeChange(mapping, link, linked, [otherConnection], DropEmail());
        await ofAnother.PlanAsync(TestContext.Current.CancellationToken);

        Assert.Equal((CodeChangeStage.WorkCloneIsLinked, CodeChangeStage.WorkCloneIsLinked), (itself.Stage, ofAnother.Stage));
        Assert.False(itself.CanWrite || ofAnother.CanWrite);
        Assert.Contains("Email", File.ReadAllText(Path.Combine(linked, "src", "Shop.Data", "Kunde.cs")), StringComparison.Ordinal);
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
