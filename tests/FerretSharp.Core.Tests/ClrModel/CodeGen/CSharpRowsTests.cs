using FerretSharp.Core.ClrModel;
using FerretSharp.Core.Data;
using FerretSharp.Core.Query;
using FerretSharp.Core.Schema;
using FerretSharp.Core.Settings;
using NSubstitute;

namespace FerretSharp.Core.Tests.ClrModel;

public sealed class CSharpRowsTests
{
    private static RowData Row(int id, string name, object? umsatz = null, object? legacy = null, object? mandant = null, object? status = null) =>
        new(new RowKey.PrimaryKey([(decimal)id]),
            [(decimal)id, name, new DateTime(2026, 10, 5, 14, 2, 13), umsatz, "N", 2m, status, legacy, mandant, null, "J ", null]);

    [Fact]
    public async Task One_row_is_a_variable_nulls_are_left_out()
    {
        var result = CSharpRows.Initializers(await CodeGenerationModel.PresentationAsync(), [Row(4711, "Meier GmbH", umsatz: 1234.50m)]);

        Assert.Equal(
            "var kunde = new Kunde\r\n" +
            "{\r\n" +
            "    KundeId = 4711,\r\n" +
            "    Name = \"Meier GmbH\",\r\n" +
            "    ErstelltAm = new DateTime(2026, 10, 5, 14, 2, 13),\r\n" +
            "    Umsatz = 1234.50m,\r\n" +
            "    Gesperrt = false,\r\n" +
            "    Kundenart = Kundenart.Gewerbe,\r\n" +
            "    Geprueft = true,\r\n" +
            "};",
            result.Text);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public async Task Several_rows_are_a_list_gaps_become_comments()
    {
        var result = CSharpRows.Initializers(await CodeGenerationModel.PresentationAsync(),
            [Row(1, "A", legacy: "X1", mandant: 3m), Row(2, "B", status: "WEG")]);

        Assert.Equal(
            "List<Kunde> kunden =\r\n" +
            "[\r\n" +
            "    new()\r\n" +
            "    {\r\n" +
            "        KundeId = 1,\r\n" +
            "        Name = \"A\",\r\n" +
            "        ErstelltAm = new DateTime(2026, 10, 5, 14, 2, 13),\r\n" +
            "        Gesperrt = false,\r\n" +
            "        Kundenart = Kundenart.Gewerbe,\r\n" +
            "        // LEGACY_CODE = 'X1' (keine Property)\r\n" +
            "        // MandantId = 3 (Shadow-Property)\r\n" +
            "        Geprueft = true,\r\n" +
            "    },\r\n" +
            "    new()\r\n" +
            "    {\r\n" +
            "        KundeId = 2,\r\n" +
            "        Name = \"B\",\r\n" +
            "        ErstelltAm = new DateTime(2026, 10, 5, 14, 2, 13),\r\n" +
            "        Gesperrt = false,\r\n" +
            "        Kundenart = Kundenart.Gewerbe,\r\n" +
            "        // Status: 'WEG' ist kein Wert von KundeStatus\r\n" +
            "        Geprueft = true,\r\n" +
            "    },\r\n" +
            "];",
            result.Text);
        Assert.Equal(
            [
                "LEGACY_CODE: keine Property – als Kommentar.",
                "MandantId: Shadow-Property, im Initializer nicht setzbar – als Kommentar.",
                "Status: 'WEG' ist kein Wert von KundeStatus – als Kommentar.",
            ],
            result.Warnings);
    }

    /// <summary>WP-29: comments in the generated code and the warnings follow the UI language; a gap in several rows is counted.</summary>
    [Fact]
    public async Task Comments_and_warnings_in_English()
    {
        var presentation = await CodeGenerationModel.PresentationAsync();
        using (UiCulture.Use("en"))
        {
            var result = CSharpRows.Initializers(presentation, [Row(1, "A", legacy: "X1"), Row(2, "B", legacy: "X2", status: "WEG")]);

            Assert.Contains("        // LEGACY_CODE = 'X1' (no property)\r\n", result.Text, StringComparison.Ordinal);
            Assert.Contains("        // Status: 'WEG' is not a value of KundeStatus\r\n", result.Text, StringComparison.Ordinal);
            Assert.Equal(
                [
                    "LEGACY_CODE: no property (2 rows) – as a comment.",
                    "Status: 'WEG' is not a value of KundeStatus – as a comment.",
                ],
                result.Warnings);
        }
    }

    [Fact]
    public async Task Without_a_db_set_the_list_is_named_after_the_entity()
    {
        var result = CSharpRows.Initializers(await CodeGenerationModel.PresentationAsync(dbSet: null), [Row(1, "A"), Row(2, "B")]);

        Assert.StartsWith("List<Kunde> kundeList =", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Seed_rows_with_shadow_values_are_anonymous()
    {
        var result = CSharpRows.HasData(await CodeGenerationModel.PresentationAsync(), [Row(1, "A"), Row(2, "B", mandant: 3m)]);

        Assert.StartsWith("builder.HasData(\r\n    new Kunde\r\n    {\r\n        KundeId = 1,", result.Text, StringComparison.Ordinal);
        Assert.Contains("},\r\n    new\r\n    {\r\n        KundeId = 2,", result.Text, StringComparison.Ordinal);
        Assert.Contains("        MandantId = 3,\r\n", result.Text, StringComparison.Ordinal);
        Assert.EndsWith("    });", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Seeding_needs_a_keyed_entity_on_a_table()
    {
        var mapping = await CodeGenerationModel.MappingAsync();

        Assert.Null(CSharpRows.HasDataUnavailable(TablePresentation.Create(CodeGenerationModel.Kunden, mapping, ClrNameDisplay.Off)));
        Assert.Equal("HasData braucht einen Schlüssel – die Entity ist keyless.",
            CSharpRows.HasDataUnavailable(TablePresentation.Create(CodeGenerationModel.View, mapping, ClrNameDisplay.Off)));
    }

    [Fact]
    public async Task Single_cells()
    {
        var presentation = await CodeGenerationModel.PresentationAsync();

        Assert.Equal("Kundenart.Behoerde", CSharpRows.Cell(presentation, CodeGenerationModel.Index("KUNDENART"), 3m).Code);
        Assert.Equal("true", CSharpRows.Cell(presentation, CodeGenerationModel.Index("GESPERRT"), "J").Code);
        Assert.Equal("null", CSharpRows.Cell(presentation, CodeGenerationModel.Index("UMSATZ"), null).Code);
        Assert.Equal("LEGACY_CODE hat keine Property im C#-Modell.", CSharpRows.Cell(presentation, CodeGenerationModel.Index("LEGACY_CODE"), "X").Problem);
    }
}
