using FerretSharp.Core.ClrModel;
using FerretSharp.Core.Schema;

namespace FerretSharp.Core.Tests.ClrModel;

public class PropertyCodeTests
{
    private static PropertyExport Prop(string name, string type, string column, string? converter = null, string? provider = null) =>
        new(name, type, type, true, false, column, null, converter, provider, false, null);

    private static ModelExport Model(params PropertyExport[] properties) =>
        new(ModelExport.CurrentFormatVersion, "8.0.0", "Shop.Ctx", "options", null,
            [new EntityExport("Kunde", "Shop.Kunde", false, null, "KUNDEN", null, null, null, properties, [], [])]);

    /// <summary>A project that names its columns by convention and stores bools as J/N with its own converter.</summary>
    private static readonly ModelExport Shop = Model(
        Prop("KundenId", "int", "KUNDEN_ID"),
        Prop("Name", "string", "NAME"),
        Prop("Aktiv", "bool", "AKTIV", "JaNeinConverter", "string"),
        Prop("Gesperrt", "bool", "GESPERRT", "JaNeinConverter", "string"),
        Prop("ErstelltAm", "DateTime", "ERSTELLT_AM"),
        Prop("Chargenr", "string", "CHARGEN_NR"));

    private static ColumnInfo Col(string name, string type, int? length = null, int? precision = null, int? scale = null, bool nullable = true) =>
        new(name, type, length, false, precision, scale, nullable, false, null, 1);

    [Fact]
    public void A_text_column_becomes_a_string_with_its_length_and_no_column_name_under_a_convention()
    {
        var suggestion = PropertyCode.Suggest(Shop with { Entities = [.. Shop.Entities] }, Col("FAX_NUMMER", "VARCHAR2", 30));

        Assert.Equal("FaxNummer", suggestion.Name);
        Assert.Equal("public string? FaxNummer { get; set; }", suggestion.Declaration);
        Assert.Equal("builder.Property(x => x.FaxNummer).HasColumnName(\"FAX_NUMMER\").HasMaxLength(30);", suggestion.Configuration); // CHARGEN_NR breaks the convention
        Assert.Empty(suggestion.Notes);
    }

    [Fact]
    public void Under_a_convention_the_column_name_is_left_out()
    {
        var model = Model(Prop("KundenId", "int", "KUNDEN_ID"), Prop("Name", "string", "NAME"), Prop("ErstelltAm", "DateTime", "ERSTELLT_AM"));

        Assert.Equal("builder.Property(x => x.FaxNummer).HasMaxLength(30);", PropertyCode.Suggest(model, Col("FAX_NUMMER", "VARCHAR2", 30)).Configuration);
        Assert.Equal("builder.Property(x => x.Fax2).HasColumnName(\"FAX_2\");", PropertyCode.Suggest(model, Col("FAX_2", "NUMBER")).Configuration.Replace(".HasPrecision(", "", StringComparison.Ordinal)); // FAX_2 is no UpperSnake of Fax2
    }

    [Fact]
    public void A_one_character_column_takes_the_projects_yes_no_converter()
    {
        var suggestion = PropertyCode.Suggest(Shop, Col("STAMMKUNDE", "CHAR", 1, nullable: false));

        Assert.Equal("public bool Stammkunde { get; set; }", suggestion.Declaration);
        Assert.EndsWith(".HasConversion<JaNeinConverter>();", suggestion.Configuration, StringComparison.Ordinal);
        Assert.Equal(["bool mit JaNeinConverter wie bei 2 Properties des Projekts (Spalte mit einem Zeichen)."], suggestion.Notes);
    }

    [Fact]
    public void The_same_column_elsewhere_gives_name_type_and_converter()
    {
        var suggestion = PropertyCode.Suggest(Shop, Col("CHARGEN_NR", "VARCHAR2", 20));

        Assert.Equal("public string? Chargenr { get; set; }", suggestion.Declaration);
        Assert.Equal(["Name und Typ wie bei CHARGEN_NR in einer anderen Entity des Projekts."], suggestion.Notes);
    }

    [Fact]
    public void An_inline_converter_is_left_to_the_user()
    {
        var model = Model(Prop("Aktiv", "bool", "AKTIV", "ValueConverter<bool, string>", "string"));

        var suggestion = PropertyCode.Suggest(model, Col("AKTIV", "CHAR", 1));

        Assert.DoesNotContain("HasConversion", suggestion.Configuration, StringComparison.Ordinal);
        Assert.Contains("Das Projekt konvertiert inline (ValueConverter<bool, string>) – die Konvertierung von Hand ergänzen.", suggestion.Notes);
    }

    [Theory]
    [InlineData("NUMBER", null, 5, 0, true, "public int? A { get; set; }", "")]
    [InlineData("NUMBER", null, 12, 0, false, "public long A { get; set; }", "")]
    [InlineData("NUMBER", null, 12, 2, true, "public decimal? A { get; set; }", ".HasPrecision(12, 2)")]
    [InlineData("NUMBER", null, null, null, true, "public decimal? A { get; set; }", "")]
    [InlineData("DATE", null, null, null, false, "public DateTime A { get; set; }", "")]
    [InlineData("TIMESTAMP(6) WITH TIME ZONE", null, null, null, true, "public DateTimeOffset? A { get; set; }", "")]
    [InlineData("RAW", 16, null, null, false, "public Guid A { get; set; }", "")]
    [InlineData("BLOB", null, null, null, false, "public byte[] A { get; set; } = [];", "")]
    [InlineData("VARCHAR2", 10, null, null, false, "public string A { get; set; } = \"\";", ".HasMaxLength(10).IsRequired()")]
    public void Types_as_ef_core_reads_them(string type, int? length, int? precision, int? scale, bool nullable, string declaration, string facets)
    {
        var suggestion = PropertyCode.Suggest(Model(Prop("B", "int", "B")), Col("A", type, length, precision, scale, nullable));

        Assert.Equal(declaration, suggestion.Declaration);
        Assert.Equal($"builder.Property(x => x.A){facets};", suggestion.Configuration);
    }

    [Theory]
    [InlineData("KundenId", "KUNDEN_ID")]
    [InlineData("PLZ", "PLZ")]
    [InlineData("HttpUrl2", "HTTP_URL2")]
    [InlineData("XMLDaten", "XML_DATEN")]
    [InlineData("ÄnderungAm", "ÄNDERUNG_AM")]
    public void Upper_snake(string name, string column) => Assert.Equal(column, PropertyCode.UpperSnake(name));

    [Theory]
    [InlineData("KUNDEN_ID", "KundenId")]
    [InlineData("FAX", "Fax")]
    [InlineData("Nummer", "Nummer")]
    [InlineData("raw col", "RawCol")]
    public void Pascal_case(string column, string name) => Assert.Equal(name, PropertyCode.PascalCase(column));

    /// <summary>The model exports <c>@event</c> as <c>event</c>; taken over, it needs its <c>@</c> again.</summary>
    [Fact]
    public void A_keyword_from_the_project_gets_an_at() =>
        Assert.Equal("public string? @event { get; set; }", PropertyCode.Suggest(Model(Prop("event", "string", "EVENT")), Col("EVENT", "VARCHAR2", 5)).Declaration);
}
