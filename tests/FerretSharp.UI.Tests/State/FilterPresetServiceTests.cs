using FerretSharp.Core.Query;
using FerretSharp.Core.Schema;
using FerretSharp.UI.State;
using Microsoft.Extensions.Logging.Abstractions;

namespace FerretSharp.UI.Tests.State;

/// <summary>Filter presets (WP-37) over the real file: per table name for every connection, save, replace, rename, delete, a broken file.</summary>
public sealed class FilterPresetServiceTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly TableSummary AuftragTest = new("ERP_TEST", "AUFTRAG", TableKind.Table);
    private static readonly TableSummary AuftragProd = new("ERP_PROD", "AUFTRAG", TableKind.Table);
    private static readonly TableSummary Kunde = new("ERP_TEST", "KUNDE", TableKind.Table);

    private static readonly FilterCondition Offen = FilterCondition.Of("STATUS", FilterOperator.Equals, "OFFEN");

    private readonly TestFolder _folder = new();

    private string StoreFile => _folder.Combine("filter-presets.json");

    public void Dispose() => _folder.Dispose();

    private async Task<FilterPresetService> ServiceAsync()
    {
        var service = new FilterPresetService(new FilterPresetStore(StoreFile), NullLogger<FilterPresetService>.Instance);
        await service.EnsureLoadedAsync();
        return service;
    }

    [Fact]
    public async Task A_preset_applies_to_the_table_of_every_schema_and_is_kept()
    {
        var service = await ServiceAsync();
        var changes = 0;
        service.Changed += () => changes++;

        var (saved, error) = await service.SaveAsync(AuftragTest, "  Offene Aufträge ", [Offen], [new SortSpec("ERSTELLT", true)]);
        await service.SaveAsync(AuftragTest, "Alle", [], [new SortSpec("ID", false)]);
        await service.SaveAsync(Kunde, "Testkunden", [FilterCondition.Of("NAME", FilterOperator.StartsWith, "Test")], []);

        Assert.Null(error);
        Assert.Equal("Offene Aufträge", saved!.Name);
        Assert.Equal(3, changes);
        Assert.Equal(["Alle", "Offene Aufträge"], service.For(AuftragProd).Select(p => p.Name)); // by name, Prod sees Test's
        var reread = await ServiceAsync();
        var preset = reread.Find(AuftragProd, "offene aufträge");
        Assert.NotNull(preset);
        Assert.Equal([Offen.Column], preset.Filters.Select(f => f.Column));
        Assert.Equal([new SortSpec("ERSTELLT", true)], preset.Sorts);
        Assert.Equal(["Testkunden"], reread.For(Kunde).Select(p => p.Name));
    }

    [Fact]
    public async Task Saving_under_a_taken_name_replaces_that_preset()
    {
        var service = await ServiceAsync();
        var (first, _) = await service.SaveAsync(AuftragTest, "Offene", [Offen], []);

        var (second, error) = await service.SaveAsync(AuftragProd, "OFFENE", [], [new SortSpec("ID", true)]);

        Assert.Null(error);
        Assert.Equal(first!.Id, second!.Id);
        var preset = Assert.Single(service.For(AuftragTest));
        Assert.Equal("OFFENE", preset.Name);
        Assert.Empty(preset.Filters);
    }

    [Fact]
    public async Task Renaming_refuses_a_name_another_preset_of_the_table_has()
    {
        var service = await ServiceAsync();
        var (offen, _) = await service.SaveAsync(AuftragTest, "Offene", [Offen], []);
        await service.SaveAsync(AuftragTest, "Alle", [], []);
        await service.SaveAsync(Kunde, "Neu", [], []);

        var taken = await service.RenameAsync(offen!, " alle ");
        var sameOtherTable = await service.RenameAsync(offen!, "Neu");
        var caseOnly = await service.RenameAsync(service.Find(AuftragTest, "Neu")!, "NEU");

        Assert.Equal("Es gibt schon eine Vorlage alle.", taken);
        Assert.Null(sameOtherTable); // KUNDE's "Neu" is another table's
        Assert.Null(caseOnly); // its own name in other case
        Assert.Equal(["Alle", "NEU"], (await ServiceAsync()).For(AuftragTest).Select(p => p.Name));
    }

    [Fact]
    public async Task Deleting_removes_only_that_preset()
    {
        var service = await ServiceAsync();
        var (offen, _) = await service.SaveAsync(AuftragTest, "Offene", [Offen], []);
        await service.SaveAsync(AuftragTest, "Alle", [], []);

        Assert.Null(await service.DeleteAsync(offen!));

        Assert.Equal(["Alle"], (await ServiceAsync()).For(AuftragTest).Select(p => p.Name));
    }

    [Fact]
    public async Task An_invalid_name_writes_nothing()
    {
        var service = await ServiceAsync();

        var (saved, error) = await service.SaveAsync(AuftragTest, "  ", [Offen], []);

        Assert.Null(saved);
        Assert.Equal("Bitte einen Namen eingeben.", error);
        Assert.False(File.Exists(StoreFile));
    }

    /// <summary>A file that could not be read is never replaced by the presets saved meanwhile; once readable it is used.</summary>
    [Fact]
    public async Task Nothing_is_saved_until_the_file_could_be_read()
    {
        const string broken = """{ "version": 1, "presets": [ { "name": "Offene", "table": "AUFTRAG", "filters": 42 } ] }""";
        await File.WriteAllTextAsync(StoreFile, broken, Ct);
        var service = await ServiceAsync();

        var (saved, error) = await service.SaveAsync(AuftragTest, "Neu", [Offen], []);

        Assert.Contains("keine gültige Datei mit Filter-Vorlagen", service.LoadError);
        Assert.Null(saved);
        Assert.Equal(service.LoadError, error);
        Assert.Equal(broken, await File.ReadAllTextAsync(StoreFile, Ct));

        await File.WriteAllTextAsync(StoreFile, """{ "version": 1, "presets": [ { "id": "6f1c1a52-6c55-4a43-9a51-6a1c2b0f9a10", "name": "Offene", "table": "AUFTRAG", "filters": [] } ] }""", Ct);
        await service.EnsureLoadedAsync(); // opening the menu again

        Assert.Null(service.LoadError);
        Assert.Equal(["Offene"], service.For(AuftragTest).Select(p => p.Name));
        Assert.Null((await service.SaveAsync(AuftragTest, "Neu", [Offen], [])).Error);
        Assert.Equal(["Neu", "Offene"], (await ServiceAsync()).For(AuftragTest).Select(p => p.Name));
    }

    /// <summary>The filter bar names the preset applied last, also after a restart: the tab keeps its id in the workspace file.</summary>
    [Fact]
    public void The_applied_preset_is_kept_with_the_tab()
    {
        var id = Guid.NewGuid();
        var tab = new TableTab(Guid.NewGuid(), AuftragTest) { Preset = id };

        var state = tab.ToState([tab]);
        var restored = TableTab.Restore(Guid.NewGuid(), AuftragTest, state);

        Assert.Equal(id, state.Preset);
        Assert.Equal(id, restored.Preset);
        Assert.Null(TableTab.Restore(Guid.NewGuid(), AuftragTest, state with { Preset = null }).Preset);
    }

    [Fact]
    public void The_tooltip_lists_the_rows_and_the_sort_order()
    {
        var preset = new FilterPreset(Guid.NewGuid(), "Offene", "AUFTRAG",
            [Offen, FilterCondition.Of("PRIO", FilterOperator.In, "1", "2") with { Enabled = false }, FilterCondition.Of("NOTIZ", FilterOperator.IsNull)],
            [new SortSpec("ERSTELLT", true), new SortSpec("ID", false)]);

        Assert.Equal("STATUS = OFFEN\n(aus) PRIO in Liste 1; 2\nNOTIZ ist NULL\nSortiert nach ERSTELLT ↓, ID ↑", FilterPresetText.Describe(preset));
        Assert.Equal("Keine Filterzeilen", FilterPresetText.Describe(preset with { Filters = [], Sorts = [] }));
    }
}
