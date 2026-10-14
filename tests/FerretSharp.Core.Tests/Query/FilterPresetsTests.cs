using FerretSharp.Core.Query;
using FerretSharp.Core.Schema;
using FerretSharp.Testing;

namespace FerretSharp.Core.Tests.Query;

/// <summary>Filter presets (WP-37): applying them to a table and the file they live in.</summary>
public class FilterPresetsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ColumnInfo Column(string name, string type, int position) =>
        new(name, type, type == "VARCHAR2" ? 50 : null, false, null, null, true, IsIdentity: false, Default: null, Position: position);

    private static readonly TableDetails Auftrag = new(new TableSummary("ERP_TEST", "AUFTRAG", TableKind.Table),
        [Column("ID", "NUMBER", 1), Column("STATUS", "VARCHAR2", 2), Column("ERSTELLT", "DATE", 3), Column("NOTIZ", "CLOB", 4)], ["ID"], [], false);

    private static FilterPreset Preset(string name, IReadOnlyList<FilterCondition> filters, IReadOnlyList<SortSpec>? sorts = null) =>
        new(Guid.NewGuid(), name, "AUFTRAG", filters, sorts ?? []);

    [Fact]
    public void Presets_are_keyed_by_the_table_name_without_owner()
    {
        Assert.Equal("AUFTRAG", FilterPresets.TableKey(new TableSummary("ERP_PROD", "AUFTRAG", TableKind.Table)));
        Assert.Equal(FilterPresets.TableKey(Auftrag.Table), FilterPresets.TableKey(new TableSummary("ERP_DEV", "AUFTRAG", TableKind.Table)));
    }

    [Fact]
    public void Applying_replaces_the_rows_and_keeps_the_sort_order_without_one()
    {
        var current = new[] { FilterCondition.Of("ID", FilterOperator.Equals, "4711") };
        var preset = Preset("Offene", [FilterCondition.Of("STATUS", FilterOperator.Equals, "OFFEN")]);

        var applied = FilterPresets.Apply(preset, Auftrag, current, append: false);

        Assert.Equal(preset.Filters, applied.Filters);
        Assert.Null(applied.Sorts);
        Assert.Empty(applied.MissingColumns);
    }

    [Fact]
    public void Appending_keeps_the_current_rows_first()
    {
        var current = new[] { FilterCondition.Of("ID", FilterOperator.Gt, "100") };
        var preset = Preset("Offene", [FilterCondition.Of("STATUS", FilterOperator.Equals, "OFFEN")], [new SortSpec("ERSTELLT", true)]);

        var applied = FilterPresets.Apply(preset, Auftrag, current, append: true);

        Assert.Equal([current[0], preset.Filters[0]], applied.Filters);
        Assert.Equal([new SortSpec("ERSTELLT", true)], applied.Sorts);
    }

    [Fact]
    public void Rows_on_columns_the_table_does_not_have_come_disabled_and_are_named()
    {
        var preset = Preset("Alt", [
            FilterCondition.Of("STATUS", FilterOperator.Equals, "OFFEN"),
            FilterCondition.Of("KUNDE_ID", FilterOperator.Equals, "1"),
            FilterCondition.Of("KUNDE_ID", FilterOperator.IsNotNull),
        ], [new SortSpec("PRIO", false)]);

        var applied = FilterPresets.Apply(preset, Auftrag, [], append: false);

        Assert.Equal([true, false, false], applied.Filters.Select(f => f.Enabled));
        Assert.Equal(["KUNDE_ID", "PRIO"], applied.MissingColumns);
        Assert.Null(applied.Sorts);
        Assert.Empty(QueryBuilder.Validate(Auftrag, applied.Filters)); // disabled rows do not block applying the rest
    }

    [Fact]
    public void Sorts_on_columns_that_cannot_be_sorted_are_left_out()
    {
        var preset = Preset("Sortiert", [], [new SortSpec("NOTIZ", false), new SortSpec("ERSTELLT", true), new SortSpec("ERSTELLT", false)]);

        var applied = FilterPresets.Apply(preset, Auftrag, [], append: false);

        Assert.Equal([new SortSpec("ERSTELLT", true)], applied.Sorts);
        Assert.Empty(applied.MissingColumns);
    }

    [Theory]
    [InlineData("Offene Aufträge", null)]
    [InlineData("   ", "Bitte einen Namen eingeben.")]
    [InlineData("0123456789012345678901234567890123456789012345678901234567890", "höchstens 60 Zeichen")]
    public void Names_are_checked(string name, string? problem)
    {
        var found = FilterPresets.NameProblem(name);

        if (problem is null)
        {
            Assert.Null(found);
        }
        else
        {
            Assert.Contains(problem, found);
        }
    }

    [Fact]
    public void Names_compare_without_case_and_surrounding_blanks()
    {
        Assert.True(FilterPresets.SameName("Offene Aufträge", " offene aufträge"));
        Assert.False(FilterPresets.SameName("Offene", "Offene Aufträge"));
    }

    [Fact]
    public async Task Presets_survive_a_round_trip()
    {
        using var folder = new TestFolder();
        var store = new FilterPresetStore(folder.Combine("data", "filter-presets.json")); // the folder does not exist yet
        Assert.Empty(await store.LoadAsync(Ct));
        var preset = Preset("Offene Aufträge", [
            FilterCondition.Of("STATUS", FilterOperator.In, "OFFEN", "NEU"),
            FilterCondition.Of("ERSTELLT", FilterOperator.Between, "1.10.2026", "31.10.2026") with { Enabled = false },
            FilterCondition.Of("NOTIZ", FilterOperator.IsNull),
        ], [new SortSpec("ERSTELLT", true)]);

        await store.SaveAsync([preset], Ct);
        var loaded = Assert.Single(await store.LoadAsync(Ct));

        Assert.Equal(preset with { Filters = loaded.Filters, Sorts = loaded.Sorts }, loaded);
        Assert.Equal(preset.Sorts, loaded.Sorts);
        Assert.Equal(preset.Filters.Count, loaded.Filters.Count);
        for (var i = 0; i < preset.Filters.Count; i++)
        {
            Assert.Equal(preset.Filters[i] with { Values = loaded.Filters[i].Values }, loaded.Filters[i]);
            Assert.Equal(preset.Filters[i].Values, loaded.Filters[i].Values);
        }

        var json = await File.ReadAllTextAsync(store.FilePath, Ct);
        Assert.Contains("\"op\": \"between\"", json); // enums as text: readable, and stable if the enum is reordered
    }

    /// <summary>Hand edits: entries set to null, or without name or table, are left out; lists set to null become empty.</summary>
    [Fact]
    public async Task Files_edited_by_hand_are_cleaned_up()
    {
        using var folder = new TestFolder();
        var file = folder.Combine("filter-presets.json");
        await File.WriteAllTextAsync(file, """
            { "version": 1, "presets": [
              null,
              { "id": "6f1c1a52-6c55-4a43-9a51-6a1c2b0f9a10", "table": "AUFTRAG", "filters": [] },
              { "id": "0b6f8f53-1a52-4c1a-8d6a-2b9e1f0c7d21", "name": "Ohne Listen", "table": "AUFTRAG", "filters": null, "sorts": null },
              { "id": "2a4b6c8d-1e3f-4a5b-9c7d-8e9f0a1b2c3d", "name": "Werte null", "table": "AUFTRAG",
                "filters": [ null, { "column": "STATUS", "op": "isNull", "values": null } ], "sorts": [ null ] }
            ] }
            """, Ct);

        var loaded = await new FilterPresetStore(file).LoadAsync(Ct);

        Assert.Equal(["Ohne Listen", "Werte null"], loaded.Select(p => p.Name));
        Assert.Empty(loaded[0].Filters);
        Assert.Empty(loaded[0].Sorts);
        var filter = Assert.Single(loaded[1].Filters);
        Assert.Empty(filter.Values);
        Assert.Empty(loaded[1].Sorts);
    }

    [Theory]
    [InlineData("""{ "version": 1, "presets": 42 }""", "keine gültige Datei mit Filter-Vorlagen", "nicht überschrieben")]
    [InlineData("""{ "version": 2, "presets": [], "somethingNew": true }""", "neueren FerretSharp-Version", "nicht überschrieben")]
    public async Task Broken_or_newer_files_are_reported_and_never_overwritten(string content, string loadError, string saveError)
    {
        using var folder = new TestFolder();
        var file = folder.Combine("filter-presets.json");
        await File.WriteAllTextAsync(file, content, Ct);
        var store = new FilterPresetStore(file);

        var load = await Assert.ThrowsAsync<IOException>(() => store.LoadAsync(Ct));
        var save = await Assert.ThrowsAsync<IOException>(() => store.SaveAsync([Preset("Neu", [])], Ct));

        Assert.Contains(loadError, load.Message);
        Assert.Contains(saveError, save.Message);
        Assert.Equal(content, await File.ReadAllTextAsync(file, Ct));
    }
}
