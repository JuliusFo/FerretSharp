using FerretSharp.Core.ClrModel;
using FerretSharp.Core.Data;
using FerretSharp.Core.Query;
using FerretSharp.Core.Schema;
using FerretSharp.Core.Settings;
using NSubstitute;

namespace FerretSharp.Core.Tests.ClrModel;

/// <summary>A KUNDEN table like the sample model's, with the cases WP-15 has to handle.</summary>
internal static class CodeGenerationModel
{
    public const string Owner = "APP";

    public static readonly TableDetails Kunden = new(new TableSummary(Owner, "KUNDEN", TableKind.Table),
        [
            new("KUNDE_ID", "NUMBER", null, false, 10, 0, false, false, null, 1),
            new("NAME", "VARCHAR2", 100, true, null, null, false, false, null, 2),
            new("ERSTELLT_AM", "DATE", null, false, null, null, false, false, null, 3),
            new("UMSATZ", "NUMBER", null, false, 12, 2, true, false, null, 4),
            new("GESPERRT", "CHAR", 1, false, null, null, false, false, null, 5),
            new("KUNDENART", "NUMBER", null, false, 2, 0, false, false, null, 6),
            new("STATUS", "VARCHAR2", 20, true, null, null, true, false, null, 7),
            new("LEGACY_CODE", "VARCHAR2", 10, true, null, null, true, false, null, 8),
            new("MANDANT_ID", "NUMBER", null, false, 5, 0, true, false, null, 9),
            new("EXTERN_ID", "RAW", 16, false, null, null, true, false, null, 10),
            new("GEPRUEFT", "CHAR", 1, false, null, null, true, false, null, 11),
            new("NOTIZ", "CLOB", null, false, null, null, true, false, null, 12),
        ],
        ["KUNDE_ID"], [], false);

    public static readonly TableDetails View = new(new TableSummary(Owner, "V_KUNDEN", TableKind.View),
        [new("KUNDE_ID", "NUMBER", null, false, 10, 0, false, false, null, 1)], [], [], false);

    private static PropertyExport Property(string name, string type, string column, bool nullable = false) =>
        new(name, type, "System." + type, nullable, false, column, null, null, null, false, null);

    public static async Task<ClrModelMapping> MappingAsync(string? dbSet = "Kunden")
    {
        var reader = Substitute.For<ISchemaReader>();
        reader.GetTablesAsync(Owner, Arg.Any<CancellationToken>()).Returns((IReadOnlyList<TableSummary>)[Kunden.Table, View.Table]);
        reader.GetSynonymTargetsAsync(Owner, Arg.Any<CancellationToken>()).Returns((IReadOnlyList<TableSummary>)[]);
        reader.GetForeignKeysAsync(Owner, Arg.Any<CancellationToken>()).Returns((IReadOnlyList<ForeignKeyInfo>)[]);
        reader.GetColumnNamesAsync(Owner, Arg.Any<CancellationToken>()).Returns((IReadOnlyDictionary<string, IReadOnlyList<string>>)
            new Dictionary<string, IReadOnlyList<string>>
            {
                ["KUNDEN"] = Kunden.Columns.Select(c => c.Name).ToList(),
                ["V_KUNDEN"] = ["KUNDE_ID"],
            });
        var schema = new SchemaCache(reader, Owner);
        await schema.LoadAsync(TestContext.Current.CancellationToken);

        var kunde = new EntityExport("Shop.Kunde", "Shop.Kunde", false, null, "KUNDEN", null, null, null,
            [
                Property("KundeId", "int", "KUNDE_ID"),
                Property("Name", "string", "NAME"),
                Property("ErstelltAm", "DateTime", "ERSTELLT_AM"),
                Property("Umsatz", "decimal", "UMSATZ", nullable: true),
                new("Gesperrt", "bool", "System.Boolean", false, false, "GESPERRT", null, "JaNeinConverter", "string", false,
                    [new("false", "False", "N"), new("true", "True", "J")]),
                new("Kundenart", "Kundenart", "Shop.Kundenart", false, false, "KUNDENART", null, null, "int", false,
                    [new("Privat", "1", "1"), new("Gewerbe", "2", "2"), new("Behoerde", "3", "3", "Behörde")]),
                new("Status", "KundeStatus", "Shop.KundeStatus", true, false, "STATUS", null, "UpperCaseEnumConverter<KundeStatus>", "string", false,
                    [new("Aktiv", "0", "AKTIV"), new("Inaktiv", "1", "INAKTIV")]),
                Property("MandantId", "int", "MANDANT_ID", nullable: true) with { IsShadow = true },
                Property("ExternId", "Guid", "EXTERN_ID", nullable: true),
                new("Geprueft", "bool", "System.Boolean", true, false, "GEPRUEFT", null, "JaNeinConverter", "string", false,
                    [new("false", "False", "N"), new("true", "True", "J")]),
                Property("Notiz", "string", "NOTIZ", nullable: true),
            ],
            ["KundeId"], [], dbSet);
        var view = new EntityExport("Shop.KundeView", "Shop.KundeView", false, null, null, null, "V_KUNDEN", null,
            [Property("KundeId", "int", "KUNDE_ID")], [], []);
        return await ClrModelMapping.BuildAsync(new ModelExport(1, "8.0.0", "Shop.Ctx", "options", null, [kunde, view]), schema,
            TestContext.Current.CancellationToken);
    }

    public static async Task<TablePresentation> PresentationAsync(string? dbSet = "Kunden") =>
        TablePresentation.Create(Kunden, await MappingAsync(dbSet), ClrNameDisplay.Beside);

    public static int Index(string column) => Kunden.Columns.Select(c => c.Name).ToList().IndexOf(column);
}

public sealed class CSharpCodeTests
{
    private static PropertyExport Of(string type, bool nullable = false) =>
        new("P", type, "System." + type, nullable, false, "C", null, null, null, false, null);

    private static readonly ColumnInfo Number = new("C", "NUMBER", null, false, null, null, true, false, null, 1);
    private static readonly ColumnInfo Text = new("C", "VARCHAR2", 100, true, null, null, true, false, null, 1);
    private static readonly ColumnInfo Date = new("C", "DATE", null, false, null, null, true, false, null, 1);
    private static readonly ColumnInfo Timestamp = new("C", "TIMESTAMP(7)", null, false, null, 7, true, false, null, 1);
    private static readonly ColumnInfo Raw = new("C", "RAW", 16, false, null, null, true, false, null, 1);

    private static string? Code(string type, ColumnInfo column, object? raw) => CSharpCode.Value(Of(type), null, column, raw).Code;

    [Fact]
    public void Numbers_take_the_property_type()
    {
        Assert.Equal("4711", Code("int", Number, 4711m));
        Assert.Equal("12345678901", Code("long", Number, 12345678901m));
        Assert.Equal("1234.50m", Code("decimal", Number, 1234.50m));
        Assert.Equal("-3m", Code("decimal", Number, -3m));
        Assert.Equal("1.5", Code("double", Number, 1.5m));
        Assert.Equal("double.NaN", Code("double", Number, double.NaN));
        Assert.Equal("0.25f", Code("float", Number, 0.25f));
        Assert.Equal("true", Code("bool", Number, 1m));
        Assert.Null(Code("int", Number, 1.5m));
        Assert.Null(Code("byte", Number, 300m));
        Assert.Equal("null", Code("int", Number, null));
    }

    [Fact]
    public void Texts_are_escaped()
    {
        Assert.Equal("\"Meier \\\"GmbH\\\"\\r\\nZeile 2\\t\\\\\"", Code("string", Text, "Meier \"GmbH\"\r\nZeile 2\t\\"));
        Assert.Equal("\"Grüße\"", Code("string", Text, "Grüße"));
        Assert.Equal("\"\\u0001\"", Code("string", Text, "\u0001"));
        Assert.Equal("'\\''", Code("char", Text, "'"));
    }

    [Fact]
    public void Dates_use_constructors_only_as_precise_as_needed()
    {
        Assert.Equal("new DateTime(2026, 10, 5)", Code("DateTime", Date, new DateTime(2026, 10, 5)));
        Assert.Equal("new DateTime(2026, 10, 5, 14, 2, 13)", Code("DateTime", Date, new DateTime(2026, 10, 5, 14, 2, 13)));
        Assert.Equal("new DateTime(2026, 10, 5, 14, 2, 13, 120)", Code("DateTime", Timestamp, new DateTime(2026, 10, 5, 14, 2, 13, 120)));
        Assert.Equal("new DateTime(2026, 10, 5, 0, 0, 0, 0, 5)", Code("DateTime", Timestamp, new DateTime(2026, 10, 5).AddTicks(50)));
        Assert.Equal("new DateTime(2026, 10, 5, 0, 0, 0, 0, 5).AddTicks(3)", Code("DateTime", Timestamp, new DateTime(2026, 10, 5).AddTicks(53)));
        Assert.Equal("new DateOnly(2026, 10, 5)", Code("DateOnly", Date, new DateTime(2026, 10, 5)));
        Assert.Equal("new DateTimeOffset(2026, 10, 5, 14, 0, 0, TimeSpan.FromHours(2))",
            Code("DateTimeOffset", Timestamp, new DateTimeOffset(2026, 10, 5, 14, 0, 0, TimeSpan.FromHours(2))));
        Assert.Equal("new DateTimeOffset(2026, 10, 5, 14, 0, 0, new TimeSpan(5, 30, 0))",
            Code("DateTimeOffset", Timestamp, new DateTimeOffset(2026, 10, 5, 14, 0, 0, new TimeSpan(5, 30, 0))));
        Assert.Equal("new TimeSpan(1, 2, 3, 4, 500)", Code("TimeSpan", Text, "+000000001 02:03:04.500000000"));
        Assert.Equal("new TimeSpan(2, 0, 0)", Code("TimeSpan", Text, "+00 02:00:00.000000"));
    }

    [Fact]
    public void Guids_read_raw_16_in_dotnet_byte_order()
    {
        var guid = Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e");

        Assert.Equal("new Guid(\"0f8fad5b-d9cb-469f-a165-70867728950e\")", Code("Guid", Raw, guid.ToByteArray()));
        Assert.Equal("new Guid(\"0f8fad5b-d9cb-469f-a165-70867728950e\")", Code("Guid", Text, "0F8FAD5B-D9CB-469F-A165-70867728950E"));
        Assert.Equal("Convert.FromHexString(\"CAFE01\")", Code("byte[]", Raw, new byte[] { 0xCA, 0xFE, 0x01 }));
    }

    [Fact]
    public void Enums_by_member_flags_combined_numbers_without_member_cast()
    {
        var kundenart = new PropertyExport("Kundenart", "Kundenart", "Shop.Kundenart", false, false, "C", null, null, "int", false,
            [new("Privat", "1", "1"), new("Gewerbe", "2", "2")]);
        var values = ValueTable.For(kundenart, Number);
        Assert.Equal("Kundenart.Gewerbe", CSharpCode.Value(kundenart, values, Number, 2m).Code);
        Assert.Equal("(Kundenart)7", CSharpCode.Value(kundenart, values, Number, 7m).Code);
        Assert.Equal("(Kundenart)(-1)", CSharpCode.Value(kundenart, values, Number, -1m).Code);

        var rechte = new PropertyExport("Rechte", "Rechte", "Shop.Rechte", false, false, "C", null, null, "int", true,
            [new("Keine", "0", "0"), new("Lesen", "1", "1"), new("Schreiben", "2", "2")]);
        Assert.Equal("Rechte.Lesen | Rechte.Schreiben", CSharpCode.Value(rechte, ValueTable.For(rechte, Number), Number, 3m).Code);

        var status = new PropertyExport("Status", "KundeStatus", "Shop.KundeStatus", false, false, "C", null, "UpperCaseEnumConverter", "string",
            false, [new("Aktiv", "0", "AKTIV")]);
        var unknown = CSharpCode.Value(status, ValueTable.For(status, Text), Text, "WEG");
        Assert.Null(unknown.Code);
        Assert.Equal("'WEG' ist kein Wert von KundeStatus", unknown.Problem);
    }

    [Fact]
    public void Other_converters_and_partly_loaded_lobs_have_no_literal()
    {
        var converted = Of("string") with { Converter = "TrimConverter" };
        Assert.Equal("eigener Converter TrimConverter, DB-Wert 'x'", CSharpCode.Value(converted, null, Text, "x").Problem);

        var clob = new ColumnInfo("C", "CLOB", null, false, null, null, true, false, null, 1);
        Assert.Equal("\"kurz\"", Code("string", clob, new LobValue("kurz", 4)));
        Assert.Equal("CLOB nur als Vorschau geladen (5.000 Zeichen)", CSharpCode.Value(Of("string"), null, clob, new LobValue("…", 5000)).Problem);
    }

    [Fact]
    public void Names_avoid_keywords() => Assert.Equal(["@class", "kunde", "@event"],
        [CSharpCode.Identifier("class"), CSharpCode.LocalName("Kunde"), CSharpCode.LocalName("Event")]);
}

public sealed class LinqFilterTests
{
    private static async Task<string> WhereAsync(params FilterCondition[] filters) =>
        LinqFilter.Build(await CodeGenerationModel.PresentationAsync(), filters).Text;

    [Fact]
    public async Task Filters_become_a_where_over_properties_with_the_grids_semantics()
    {
        var code = await WhereAsync(
            FilterCondition.Of("NAME", FilterOperator.Contains, "Meier"),
            FilterCondition.Of("ERSTELLT_AM", FilterOperator.Equals, "01.10.2026"),
            FilterCondition.Of("KUNDENART", FilterOperator.Equals, "2"),
            FilterCondition.Of("GESPERRT", FilterOperator.Equals, "N"));

        Assert.Equal(
            ".Where(x => x.Name.ToUpper().Contains(\"MEIER\")\r\n" +
            "         && x.ErstelltAm >= new DateTime(2026, 10, 1) && x.ErstelltAm < new DateTime(2026, 10, 2)\r\n" +
            "         && x.Kundenart == Kundenart.Gewerbe\r\n" +
            "         && !x.Gesperrt)",
            code);
    }

    [Theory]
    [InlineData("KUNDE_ID", FilterOperator.NotEquals, new[] { "4711" }, "x.KundeId != 4711")]
    [InlineData("KUNDE_ID", FilterOperator.Between, new[] { "1", "1000" }, "x.KundeId >= 1 && x.KundeId <= 1000")]
    [InlineData("UMSATZ", FilterOperator.Gt, new[] { "1.234,5" }, "x.Umsatz > 1234.5m")]
    [InlineData("UMSATZ", FilterOperator.IsNull, new string[0], "x.Umsatz == null")]
    [InlineData("NAME", FilterOperator.Equals, new[] { "Meier" }, "x.Name == \"Meier\"")]
    [InlineData("NAME", FilterOperator.StartsWith, new[] { "me" }, "x.Name.ToUpper().StartsWith(\"ME\")")]
    [InlineData("NAME", FilterOperator.Between, new[] { "A", "M" }, "string.Compare(x.Name, \"A\") >= 0 && string.Compare(x.Name, \"M\") <= 0")]
    [InlineData("NAME", FilterOperator.In, new[] { "A", "B" }, "new string[] { \"A\", \"B\" }.Contains(x.Name)")]
    [InlineData("KUNDENART", FilterOperator.In, new[] { "1", "3" }, "new Kundenart[] { Kundenart.Privat, Kundenart.Behoerde }.Contains(x.Kundenart)")]
    [InlineData("STATUS", FilterOperator.NotEquals, new[] { "AKTIV" }, "x.Status != KundeStatus.Aktiv")]
    [InlineData("GESPERRT", FilterOperator.NotEquals, new[] { "N" }, "x.Gesperrt")]
    [InlineData("GEPRUEFT", FilterOperator.Equals, new[] { "J" }, "x.Geprueft == true")]
    [InlineData("ERSTELLT_AM", FilterOperator.Equals, new[] { "2026-10-01 08:30" }, "x.ErstelltAm == new DateTime(2026, 10, 1, 8, 30, 0)")]
    [InlineData("ERSTELLT_AM", FilterOperator.Lte, new[] { "01.10.2026" }, "x.ErstelltAm < new DateTime(2026, 10, 2)")]
    [InlineData("ERSTELLT_AM", FilterOperator.Gt, new[] { "01.10.2026" }, "x.ErstelltAm >= new DateTime(2026, 10, 2)")]
    [InlineData("ERSTELLT_AM", FilterOperator.NotEquals, new[] { "01.10.2026" }, "(x.ErstelltAm < new DateTime(2026, 10, 1) || x.ErstelltAm >= new DateTime(2026, 10, 2))")]
    [InlineData("ERSTELLT_AM", FilterOperator.Between, new[] { "01.10.2026", "31.10.2026" }, "x.ErstelltAm >= new DateTime(2026, 10, 1) && x.ErstelltAm < new DateTime(2026, 11, 1)")]
    [InlineData("ERSTELLT_AM", FilterOperator.In, new[] { "01.10.2026", "2026-10-03 12:00" }, "((x.ErstelltAm >= new DateTime(2026, 10, 1) && x.ErstelltAm < new DateTime(2026, 10, 2)) || x.ErstelltAm == new DateTime(2026, 10, 3, 12, 0, 0))")]
    [InlineData("MANDANT_ID", FilterOperator.Equals, new[] { "3" }, "EF.Property<int?>(x, \"MandantId\") == 3")]
    [InlineData("EXTERN_ID", FilterOperator.Equals, new[] { "5BAD8F0FCBD99F46A16570867728950E" }, "x.ExternId == new Guid(\"0f8fad5b-d9cb-469f-a165-70867728950e\")")]
    public async Task Each_operator(string column, FilterOperator op, string[] values, string expected) =>
        Assert.Equal($".Where(x => {expected})", await WhereAsync(new FilterCondition(column, op, values)));

    [Fact]
    public async Task Untranslatable_filters_stay_as_comments_disabled_ones_are_ignored()
    {
        var result = LinqFilter.Build(await CodeGenerationModel.PresentationAsync(),
        [
            FilterCondition.Of("LEGACY_CODE", FilterOperator.Equals, "X1"),
            FilterCondition.Of("STATUS", FilterOperator.Equals, "WEG"),
            FilterCondition.Of("KUNDENART", FilterOperator.Contains, "2"),
            FilterCondition.Of("KUNDE_ID", FilterOperator.Equals, "4711"),
            FilterCondition.Of("NAME", FilterOperator.Equals, "aus") with { Enabled = false },
        ]);

        Assert.Equal(
            "// Nicht übernommen: LEGACY_CODE = X1: keine Property\r\n" +
            "// Nicht übernommen: STATUS = WEG: 'WEG' ist kein Wert von KundeStatus\r\n" +
            "// Nicht übernommen: KUNDENART enthält 2: „enthält“ nur für string-Properties (Kundenart)\r\n" +
            ".Where(x => x.KundeId == 4711)",
            result.Text);
        Assert.Equal(1, result.Rows);
        Assert.Equal(3, result.Warnings.Count);
    }

    [Fact]
    public async Task With_a_source_it_is_a_query_for_the_console()
    {
        var presentation = await CodeGenerationModel.PresentationAsync();
        var filters = new[] { FilterCondition.Of("KUNDE_ID", FilterOperator.Gt, "1"), FilterCondition.Of("KUNDENART", FilterOperator.Equals, "1") };

        Assert.Equal("db.Kunden", LinqFilter.Source(presentation));
        Assert.Equal(
            "db.Kunden\r\n" +
            "    .Where(x => x.KundeId > 1\r\n" +
            "             && x.Kundenart == Kundenart.Privat)",
            LinqFilter.Build(presentation, filters, LinqFilter.Source(presentation)).Text);
        Assert.Equal("db.Kunden", LinqFilter.Build(presentation, [], "db.Kunden").Text);
        Assert.Equal("db.Set<Kunde>()", LinqFilter.Source(await CodeGenerationModel.PresentationAsync(dbSet: null)));
    }

    [Fact]
    public void Without_an_entity_there_is_nothing_to_generate() =>
        Assert.NotNull(LinqFilter.Unavailable(TablePresentation.Plain(CodeGenerationModel.Kunden)));
}

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
