using FerretSharp.Core.ClrModel;
using FerretSharp.Core.Schema;
using FerretSharp.Core.Settings;
using FerretSharp.UI.Resources;
using FerretSharp.UI.State;
using NSubstitute;

namespace FerretSharp.UI.Tests.State;

/// <summary>WP-40: what the "Usage" view, the columns view, the designer and the editor jump make of the index.</summary>
public sealed class CodeUsageRowsTests : IDisposable
{
    private const string Owner = "APP";

    private readonly TestFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    private static readonly TableDetails Kunden = new(new TableSummary(Owner, "KUNDEN", TableKind.Table),
        [
            new("KUNDE_ID", "NUMBER", null, false, 10, 0, false, false, null, 1),
            new("NAME", "VARCHAR2", 100, true, null, null, false, false, null, 2),
            new("EMAIL", "VARCHAR2", 200, true, null, null, false, false, null, 3),
            new("NOTIZ", "VARCHAR2", 200, true, null, null, false, false, null, 4),
        ],
        ["KUNDE_ID"], [], false);

    private static PropertyExport Property(string name, string column) => new(name, "string", "System.String", true, false, column, null, null, null, false, null);

    private static readonly ModelExport Model = new(ModelExport.CurrentFormatVersion, "8.0.0", "Shop.Ctx", "options", null,
    [
        new EntityExport("Kunde", "Shop.Kunde", false, null, "KUNDEN", null, null, null,
            [Property("KundeId", "KUNDE_ID"), Property("Name", "NAME"), Property("Email", "EMAIL")], ["KundeId"], [], "Kunden"),
    ]);

    private static CodeUse Use(string member, string caller, int line, CodeUseKind kind = CodeUseKind.Read, string type = "Shop.Kunde") =>
        new(type, member, kind, "Shop.Services." + caller, "Run", @"C:\repo\src\Services\" + caller + ".cs", line);

    private static AssemblyUses Assembly(string name, bool isTest, params CodeUse[] uses) =>
        new(AssemblyUses.CurrentFormatVersion, Guid.NewGuid(), name, [], isTest, true, [], uses);

    private static async Task<(ClrModelMapping Mapping, CodeUsageIndex Index)> IndexAsync()
    {
        var reader = Substitute.For<ISchemaReader>();
        reader.GetTablesAsync(Owner, Arg.Any<CancellationToken>()).Returns((IReadOnlyList<TableSummary>)[Kunden.Table]);
        reader.GetSynonymTargetsAsync(Owner, Arg.Any<CancellationToken>()).Returns(SynonymTargets.None);
        reader.GetForeignKeysAsync(Owner, Arg.Any<CancellationToken>()).Returns((IReadOnlyList<ForeignKeyInfo>)[]);
        reader.GetDetailsAsync(Arg.Any<TableSummary>(), Arg.Any<CancellationToken>()).Returns(Kunden);
        reader.GetColumnsAsync(Owner, Arg.Any<CancellationToken>()).Returns((IReadOnlyDictionary<string, IReadOnlyList<ColumnInfo>>)
            new Dictionary<string, IReadOnlyList<ColumnInfo>> { ["KUNDEN"] = Kunden.Columns });
        var schema = new SchemaCache(reader, Owner);
        await schema.LoadAsync(TestContext.Current.CancellationToken);
        var mapping = await ClrModelMapping.BuildAsync(Model, schema, TestContext.Current.CancellationToken);
        var index = CodeUsageIndex.Build(Model,
        [
            Assembly("Shop", false, Use("Name", "KundenService", 12), Use("Name", "Export", 40, CodeUseKind.Expression), Use("Name", "Import", 7, CodeUseKind.Write),
                Use("Name", "Report", 3), Use("KundeId", "KundenService", 20)),
            Assembly("Shop.Tests", true, Use("Email", "KundenTests", 30, CodeUseKind.Write)),
        ]);
        return (mapping, index);
    }

    [Fact]
    public async Task Columns_come_in_schema_order_with_their_uses_and_whether_code_uses_them()
    {
        var (mapping, index) = await IndexAsync();

        var rows = CodeUsageRows.Columns(mapping, index, Kunden);

        Assert.Equal(["KUNDE_ID", "NAME", "EMAIL", "NOTIZ"], rows.Select(r => r.Column));
        Assert.Equal(4, rows[1].Counted);
        Assert.False(rows[1].Unused);
        Assert.Equal((0, 1, true), (rows[2].Counted, rows[2].Uses.Count, rows[2].Unused)); // only a test writes it
        Assert.Null(rows[3].Property);
        Assert.False(rows[3].Unused);
    }

    [Fact]
    public async Task The_sub_tabs_count_unused_columns_and_queries_of_application_code()
    {
        var (mapping, index) = await IndexAsync();
        CodeUsage Query(bool test = false, bool mapping = false) =>
            new(new CodeUse("Shop.Kunde", "", CodeUseKind.Query, "Shop.Services.X", "Run", null, null), "Shop", test, mapping, null);

        Assert.Equal(1, CodeUsageRows.UnusedCount(CodeUsageRows.Columns(mapping, index, Kunden))); // EMAIL: only a test writes it
        Assert.Equal(1, CodeUsageRows.QueryCount([Query(), Query(test: true), Query(mapping: true)]));
    }

    [Fact]
    public async Task Dropping_or_renaming_a_used_column_warns_with_the_first_places_tests_included()
    {
        var (mapping, index) = await IndexAsync();

        using (UiCulture.Use("en"))
        {
            var notes = CodeUsageRows.DropNotes(mapping, index, Kunden.Table.Ref, [("NAME", null), ("EMAIL", "MAIL"), ("NOTIZ", null)]);
            Assert.Equal(
            [
                "Column NAME (Name) is used in the code at 4 places (Export.Run, Import.Run, KundenService.Run, …): the build breaks once the property is removed.",
                "Column EMAIL (Email) is renamed and used in the code at 1 places (KundenTests.Run): the mapping of the property must follow.",
            ], notes);
        }
    }

    [Fact]
    public async Task The_project_line_counts_what_is_missing_and_names_it_in_the_tooltip()
    {
        var (_, index) = await IndexAsync();
        var result = new CodeUsageResult("Shop.slnx",
        [
            new CodeUsageProject(@"C:\repo\Shop\Shop.csproj", new ProjectBuild(@"C:\repo\Shop\Shop.csproj", "Shop", @"C:\repo\Shop\bin\Shop.dll", DateTime.UtcNow, null), null, null),
            new CodeUsageProject(@"C:\repo\Web\Web.csproj", new ProjectBuild(@"C:\repo\Web\Web.csproj", "Web", null, null, null), null, null),
            new CodeUsageProject(@"C:\repo\Api\Api.csproj", new ProjectBuild(@"C:\repo\Api\Api.csproj", "Api", @"C:\repo\Api\bin\Api.dll", DateTime.UtcNow, @"C:\repo\Api\Changed.cs"), null, null),
        ], index);

        using (UiCulture.Use("en"))
        {
            Assert.Equal("3 projects · 1 not built · 1 out of date", CodeUsageRows.Projects(result));
            Assert.Equal(["Web: " + ModelViewText.Usage_NotBuilt, "Api: out of date: Changed.cs changed after the build"], CodeUsageRows.ProjectDetails(result).Split('\n'));
        }
    }

    [Fact]
    public void Caller_location_and_name_read_as_in_the_code()
    {
        var use = Use("Name", "KundenService", 12);

        Assert.Equal("KundenService.Run", CodeUsageRows.Caller(use));
        Assert.Equal("KundenService.cs:12", CodeUsageRows.Location(use));
        Assert.Equal("", CodeUsageRows.Location(use with { Document = null }));
        Assert.Equal("Kunde.Name", CodeUsageRows.Name(Model.Entities[0], "Name"));
    }

    [Fact]
    public async Task A_use_opens_in_the_groups_work_clone_with_the_solution_there()
    {
        var linkedRoot = Clone("test-db");
        var work = Clone("work");
        var link = new ClrProjectLink(Path.Combine(linkedRoot, "src", "Shop.Data", "Shop.Data.csproj"));
        var clones = new WorkClones(_folder.Combine("work-clones.json"));
        await clones.SetActiveAsync("ERP", work, TestContext.Current.CancellationToken);
        var launcher = Substitute.For<ICodeEditorLauncher>();
        launcher.OpenAsync(Arg.Any<CodeEditor>(), Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(new CodeOpenResult(CodeOpenOutcome.Opened));
        var settings = new AppSettingsService(new SettingsStore(_folder.Combine("settings.json")), new AppSettings());
        var opener = new CodeUsageOpener(launcher, clones, settings);
        var use = new CodeUse("Shop.Kunde", "Name", CodeUseKind.Read, "Shop.Kunde", "Get", Path.Combine(linkedRoot, "src", "Shop.Data", "Kunde.cs"), 7);

        var notice = await opener.OpenAsync(use, link, Path.Combine(linkedRoot, "Server.slnx"), "erp", TestContext.Current.CancellationToken);

        Assert.Null(notice);
        await launcher.Received(1).OpenAsync(CodeEditor.VisualStudio, Path.Combine(work, "src", "Shop.Data", "Kunde.cs"), 7, Path.Combine(work, "Server.slnx"), Arg.Any<CancellationToken>());

        // Another group has no work clone: the linked clone itself.
        launcher.ClearReceivedCalls();
        Assert.Null(await opener.OpenAsync(use, link, Path.Combine(linkedRoot, "Server.slnx"), "Kasse", TestContext.Current.CancellationToken));
        await launcher.Received(1).OpenAsync(CodeEditor.VisualStudio, use.Document!, 7, Path.Combine(linkedRoot, "Server.slnx"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_file_found_by_its_type_opens_without_the_line_and_says_so()
    {
        var linkedRoot = Clone("test-db");
        var work = Clone("work");
        File.Move(Path.Combine(work, "src", "Shop.Data", "Kunde.cs"), Path.Combine(work, "src", "Shop.Data", "Moved.cs"));
        var link = new ClrProjectLink(Path.Combine(linkedRoot, "src", "Shop.Data", "Shop.Data.csproj"));
        var clones = new WorkClones(_folder.Combine("work-clones.json"));
        await clones.SetActiveAsync(null, work, TestContext.Current.CancellationToken);
        var launcher = Substitute.For<ICodeEditorLauncher>();
        launcher.OpenAsync(Arg.Any<CodeEditor>(), Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(new CodeOpenResult(CodeOpenOutcome.OpenedElsewhere));
        var opener = new CodeUsageOpener(launcher, clones, new AppSettingsService(new SettingsStore(_folder.Combine("settings.json")), new AppSettings()));
        var use = new CodeUse("Shop.Kunde", "Name", CodeUseKind.Read, "Shop.Kunde", "Get", Path.Combine(linkedRoot, "src", "Shop.Data", "Kunde.cs"), 7);

        var notice = await opener.OpenAsync(use, link, null, null, TestContext.Current.CancellationToken);

        Assert.Equal(ModelViewText.Usage_OpenedElsewhere, notice);
        await launcher.Received(1).OpenAsync(CodeEditor.VisualStudio, Path.Combine(work, "src", "Shop.Data", "Moved.cs"), null, null, Arg.Any<CancellationToken>());
    }

    private string Clone(string name)
    {
        var root = Directory.CreateDirectory(_folder.Combine(name)).FullName;
        Directory.CreateDirectory(Path.Combine(root, ".git"));
        File.WriteAllText(Path.Combine(root, ".git", "HEAD"), "ref: refs/heads/main");
        Directory.CreateDirectory(Path.Combine(root, "src", "Shop.Data"));
        File.WriteAllText(Path.Combine(root, "src", "Shop.Data", "Shop.Data.csproj"), "<Project />");
        File.WriteAllText(Path.Combine(root, "src", "Shop.Data", "Kunde.cs"), "namespace Shop; public class Kunde { }");
        File.WriteAllText(Path.Combine(root, "Server.slnx"), "<Solution />");
        return root;
    }
}
