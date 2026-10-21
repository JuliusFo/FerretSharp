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
    /// <summary>Uses that count: outside tests and mapping code.</summary>
    private static int Counted(ColumnUsageRow row) => row.Uses.Count(u => !u.IsTest && !u.IsMapping);

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

    private static readonly TableDetails Auftraege = new(new TableSummary(Owner, "AUFTRAEGE", TableKind.Table),
        [
            new("AUFTRAG_ID", "NUMBER", null, false, 10, 0, false, false, null, 1),
            new("KUNDE_ID", "NUMBER", null, false, 10, 0, false, false, null, 2),
            new("BETRAG", "NUMBER", null, true, 10, 2, false, false, null, 3),
        ],
        ["AUFTRAG_ID"], [], false);

    private static PropertyExport Property(string name, string column) => new(name, "string", true, false, column, null, null, null, false, null);

    private static readonly ModelExport Model = new(ModelExport.CurrentFormatVersion, "8.0.0", "Shop.Ctx", "options", null,
    [
        new EntityExport("Kunde", "Shop.Kunde", false, null, "KUNDEN", null, null, null,
            [Property("KundeId", "KUNDE_ID"), Property("Name", "NAME"), Property("Email", "EMAIL")], ["KundeId"], [], "Kunden"),
    ]);

    private static CodeUse Use(string member, string caller, int line, CodeUseKind kind = CodeUseKind.Read, string type = "Shop.Kunde") =>
        new(type, member, kind, "Shop.Services." + caller, "Run", @"C:\repo\src\Services\" + caller + ".cs", line);

    private static AssemblyUses Assembly(string name, bool isTest, params CodeUse[] uses) =>
        new(AssemblyUses.CurrentFormatVersion, Guid.NewGuid(), name, [], isTest, true, [], uses);

    /// <summary>Kunde plus Auftrag with a foreign key to it, both navigations named (<c>Auftrag.Kunde</c>, <c>Kunde.Auftraege</c>).</summary>
    private static readonly ModelExport WithOrders = Model with
    {
        Entities =
        [
            .. Model.Entities,
            new EntityExport("Auftrag", "Shop.Auftrag", false, null, "AUFTRAEGE", null, null, null,
                [Property("AuftragId", "AUFTRAG_ID"), Property("KundeId", "KUNDE_ID"), Property("Betrag", "BETRAG")], ["AuftragId"],
                [new ForeignKeyExport(["KundeId"], "Kunde", ["KundeId"], "Kunde", "Auftraege", false)]),
        ],
    };

    private static async Task<ClrModelMapping> MappingAsync(ModelExport model, params TableDetails[] tables)
    {
        var reader = Substitute.For<ISchemaReader>();
        reader.GetTablesAsync(Owner, Arg.Any<CancellationToken>()).Returns((IReadOnlyList<TableSummary>)[.. tables.Select(t => t.Table)]);
        reader.GetSynonymTargetsAsync(Owner, Arg.Any<CancellationToken>()).Returns(SynonymTargets.None);
        reader.GetForeignKeysAsync(Arg.Is<IReadOnlyList<string>>(o => o.Contains(Owner)), Arg.Any<CancellationToken>()).Returns((IReadOnlyList<ForeignKeyInfo>)[]);
        reader.GetDetailsAsync(Arg.Any<TableSummary>(), Arg.Any<CancellationToken>()).Returns(c => tables.Single(t => t.Table == c.Arg<TableSummary>()));
        reader.GetColumnsAsync(Owner, Arg.Any<CancellationToken>()).Returns((IReadOnlyDictionary<string, IReadOnlyList<ColumnInfo>>)
            tables.ToDictionary(t => t.Table.Name, t => t.Columns));
        var schema = new SchemaCache(reader, Owner);
        await schema.LoadAsync(TestContext.Current.CancellationToken);
        return await ClrModelMapping.BuildAsync(model, schema, TestContext.Current.CancellationToken);
    }

    private static async Task<(ClrModelMapping Mapping, CodeUsageIndex Index)> IndexAsync()
    {
        var mapping = await MappingAsync(Model, Kunden);
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
        Assert.Equal(4, Counted(rows[1]));
        Assert.False(rows[1].Unused);
        Assert.Equal((0, 1, true), (Counted(rows[2]), rows[2].Uses.Count, rows[2].Unused)); // only a test writes it
        Assert.Null(rows[3].Property);
        Assert.False(rows[3].Unused);
    }

    [Fact]
    public async Task The_filter_splits_reads_with_linq_from_writes_and_counts_tests_apart()
    {
        var (mapping, index) = await IndexAsync();
        var rows = CodeUsageRows.Columns(mapping, index, Kunden);
        var name = rows[1];

        Assert.Equal((3, 1), (name.Count(UsageAccess.Read, tests: false), name.Count(UsageAccess.Write, tests: false)));
        Assert.Equal(["Export.Run", "KundenService.Run", "Report.Run"], name.Matching(UsageAccess.Read).Select(u => CodeUsageRows.Caller(u.Use)).Order());
        Assert.Equal(["Import.Run"], name.Matching(UsageAccess.Write).Select(u => CodeUsageRows.Caller(u.Use)));
        Assert.Equal((0, 1), (rows[2].Count(UsageAccess.Write, tests: false), rows[2].Count(UsageAccess.Write, tests: true)));

        // Rows without such a use drop out; a test's write still shows (the build of the tests breaks too).
        Assert.Equal(["KUNDE_ID", "NAME"], CodeUsageRows.Filter(rows, UsageAccess.Read).Select(r => r.Column));
        Assert.Equal(["NAME", "EMAIL"], CodeUsageRows.Filter(rows, UsageAccess.Write).Select(r => r.Column));
        Assert.Same(rows, CodeUsageRows.Filter(rows, UsageAccess.All));
    }

    [Fact]
    public void Mapping_code_is_neither_read_nor_write()
    {
        var configure = new CodeUsage(Use("Email", "KundeConfiguration", 4, CodeUseKind.Expression), "Shop", false, IsMapping: true, null);

        Assert.True(CodeUsageRows.Matches(configure, UsageAccess.All));
        Assert.False(CodeUsageRows.Matches(configure, UsageAccess.Read));
        Assert.False(CodeUsageRows.Matches(configure, UsageAccess.Write));
        Assert.True(CodeUsageRows.Matches(configure with { IsMapping = false }, UsageAccess.Read));
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
    public async Task A_foreign_key_column_counts_its_navigations_and_dropping_it_names_them()
    {
        var mapping = await MappingAsync(WithOrders, Kunden, Auftraege);
        var auftrag = WithOrders.Entities.Single(e => e.Name == "Auftrag");
        var navigations = new[] { Use("Kunde", "Rechnung", 5, type: "Shop.Auftrag"), Use("Auftraege", "KundenService", 30, type: "Shop.Kunde") };
        var index = CodeUsageIndex.Build(WithOrders, [Assembly("Shop", false, navigations)]);

        var row = Assert.Single(CodeUsageRows.Columns(mapping, index, Auftraege), r => r.Column == "KUNDE_ID");
        Assert.Equal((2, false), (Counted(row), row.Unused));
        Assert.Equal(["Kunde.Auftraege", "Auftrag.Kunde"], row.Uses.Select(u => CodeUsageRows.Name(u, auftrag, "KundeId")));
        Assert.Equal("Auftrag.Betrag", CodeUsageRows.Name(new CodeUsage(navigations[0], "Shop", false, false, null), auftrag, "Betrag"));

        using (UiCulture.Use("en"))
        {
            // Only navigations: the build does not break, the relationship does.
            Assert.Equal(
            [
                "Column KUNDE_ID is used in the code through Kunde.Auftraege, Auftrag.Kunde at 2 places (KundenService.Run, Rechnung.Run): "
                + "the relationship needs the column, their queries fail at run time without it.",
            ], CodeUsageRows.DropNotes(mapping, index, Auftraege.Table.Ref, [("KUNDE_ID", null)]));

            var both = CodeUsageIndex.Build(WithOrders, [Assembly("Shop", false, [.. navigations, Use("KundeId", "Import", 8, type: "Shop.Auftrag")])]);
            Assert.Equal(
            [
                "Column KUNDE_ID (KundeId) is used in the code at 1 places (Import.Run): the build breaks once the property is removed. "
                + "2 more through Kunde.Auftraege, Auftrag.Kunde: the relationship needs the column, their queries fail at run time without it.",
            ], CodeUsageRows.DropNotes(mapping, both, Auftraege.Table.Ref, [("KUNDE_ID", null)]));
        }
    }

    [Fact]
    public async Task The_project_line_counts_what_is_missing_and_names_it_in_the_tooltip()
    {
        var (_, index) = await IndexAsync();
        var result = new CodeUsageResult("Shop.slnx",
        [
            Project("Shop", built: true),
            Project("Web", built: false),
            Project("Api", built: true, newer: "Changed.cs"),
        ], index);

        using (UiCulture.Use("en"))
        {
            Assert.Equal("3 projects · 1 not built · 1 out of date", CodeUsageRows.Projects(result));
            Assert.Equal(["Web: " + ModelViewText.Usage_NotBuilt, "Api: out of date: Changed.cs changed after the build"], CodeUsageRows.ProjectDetails(result).Split('\n'));
        }
    }

    /// <summary>A project below a temp root, built with Path.Combine (the CI runs the UI tests on Linux too).</summary>
    private static CodeUsageProject Project(string name, bool built, string? newer = null)
    {
        var folder = Path.Combine(Path.GetTempPath(), "repo", name);
        var file = Path.Combine(folder, name + ".csproj");
        return new CodeUsageProject(file, new ProjectBuild(file, name, built ? Path.Combine(folder, "bin", name + ".dll") : null,
            built ? DateTime.UtcNow : null, newer is null ? null : Path.Combine(folder, newer)), null, null);
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
