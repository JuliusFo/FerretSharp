using FerretSharp.Core.ClrModel;
using FerretSharp.Core.Schema;
using NSubstitute;

namespace FerretSharp.Core.Tests.ClrModel;

public sealed class ClrModelMappingTests
{
    private const string Owner = "APP";

    private readonly ISchemaReader _reader = Substitute.For<ISchemaReader>();
    private readonly Dictionary<string, IReadOnlyList<string>> _columns = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static PropertyExport Prop(string name, string? column, string type = "int") =>
        new(name, type, type, false, false, column, null, null, null, false, null);

    private static EntityExport Entity(string clrType, string? table, params PropertyExport[] properties) =>
        new(clrType, clrType, false, null, table, null, null, null, properties, [], []);

    private static ModelExport Model(params EntityExport[] entities) => new(1, "8.0.11", "Shop.AppDbContext", "options", null, entities);

    private async Task<SchemaCache> SchemaAsync(params TableSummary[] tables)
    {
        _reader.GetTablesAsync(Owner, Arg.Any<CancellationToken>()).Returns(tables.Where(t => t.Synonym is null).ToList());
        _reader.GetSynonymTargetsAsync(Owner, Arg.Any<CancellationToken>()).Returns(tables.Where(t => t.Synonym is not null).ToList());
        _reader.GetForeignKeysAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns([]);
        _reader.GetDetailsAsync(Arg.Any<TableSummary>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            var table = call.Arg<TableSummary>();
            var columns = _columns[table.Name].Select((c, i) => new ColumnInfo(c, "NUMBER", null, false, 10, 0, true, false, null, i + 1)).ToList();
            return new TableDetails(table, columns, [], [], false);
        });
        var schema = new SchemaCache(_reader, Owner);
        await schema.LoadAsync(Ct);
        return schema;
    }

    [Fact]
    public async Task Entities_and_properties_map_to_tables_and_columns_and_differences_are_listed()
    {
        _columns["KUNDEN"] = ["KUNDE_ID", "NAME", "ANZAHL"];
        var schema = await SchemaAsync(new TableSummary(Owner, "KUNDEN", TableKind.Table));
        var model = Model(
            Entity("Shop.Kunde", "KUNDEN", Prop("KundeId", "KUNDE_ID"), Prop("Name", "NAME", "string"), Prop("Email", "EMAIL", "string")),
            Entity("Shop.Newsletter", "NEWSLETTER", Prop("Id", "ID")));

        var mapping = await ClrModelMapping.BuildAsync(model, schema, Ct);

        var kunden = new TableRef(Owner, "KUNDEN");
        Assert.Equal("Shop.Kunde", mapping.EntityOf(kunden)!.Entity.ClrType);
        Assert.Equal("Name", mapping.PropertyOf(kunden, "NAME")!.Name);
        Assert.Null(mapping.PropertyOf(kunden, "ANZAHL"));
        Assert.Equal(
            [
                (MappingIssueKind.EntityWithoutTable, "Newsletter: Tabelle NEWSLETTER gibt es nicht."),
                (MappingIssueKind.PropertyWithoutColumn, "Kunde.Email: Spalte KUNDEN.EMAIL gibt es nicht."),
                (MappingIssueKind.ColumnWithoutProperty, "KUNDEN.ANZAHL: keine Property in Kunde."),
            ],
            mapping.Issues.Select(i => (i.Kind, i.Message)));
    }

    [Fact]
    public async Task An_unqualified_name_finds_a_synonym_of_the_schema()
    {
        _columns["KUNDEN"] = ["ID"];
        var schema = await SchemaAsync(new TableSummary("STAMM", "KUNDEN", TableKind.Table, new SynonymInfo(Owner, "KUNDEN")));

        var mapping = await ClrModelMapping.BuildAsync(Model(Entity("Shop.Kunde", "KUNDEN", Prop("Id", "ID"))), schema, Ct);

        Assert.Equal("Shop.Kunde", mapping.EntityOf(new TableRef("STAMM", "KUNDEN"))!.Entity.ClrType);
        Assert.Empty(mapping.Issues);
    }

    [Fact]
    public async Task Names_differing_only_in_case_are_matched_but_reported()
    {
        _columns["KUNDEN"] = ["KUNDE_ID"];
        var schema = await SchemaAsync(new TableSummary(Owner, "KUNDEN", TableKind.Table));

        var mapping = await ClrModelMapping.BuildAsync(Model(Entity("Shop.Kunde", "Kunden", Prop("KundeId", "Kunde_Id"))), schema, Ct);

        Assert.Equal("KundeId", mapping.PropertyOf(new TableRef(Owner, "KUNDEN"), "KUNDE_ID")!.Name);
        Assert.Equal([MappingIssueKind.CaseMismatch, MappingIssueKind.CaseMismatch], mapping.Issues.Select(i => i.Kind));
        Assert.Contains("EF Core quotet Namen", mapping.Issues[0].Message);
    }

    [Fact]
    public async Task Entities_sharing_a_table_count_their_columns_together_and_the_owner_wins()
    {
        _columns["KUNDEN"] = ["ID", "STRASSE"];
        var schema = await SchemaAsync(new TableSummary(Owner, "KUNDEN", TableKind.Table));
        var owned = Entity("Shop.Adresse", "KUNDEN", Prop("Strasse", "STRASSE", "string")) with { IsOwned = true };

        var mapping = await ClrModelMapping.BuildAsync(Model(owned, Entity("Shop.Kunde", "KUNDEN", Prop("Id", "ID"))), schema, Ct);

        Assert.Equal("Shop.Kunde", mapping.EntityOf(new TableRef(Owner, "KUNDEN"))!.Entity.ClrType);
        Assert.Equal("Strasse", mapping.PropertyOf(new TableRef(Owner, "KUNDEN"), "STRASSE")!.Name);
        Assert.Empty(mapping.Issues);
    }

    [Fact]
    public async Task A_default_schema_or_an_explicit_one_is_looked_up_there_and_views_count_too()
    {
        _columns["V_UMSATZ"] = ["SUMME"];
        var schema = await SchemaAsync(new TableSummary(Owner, "V_UMSATZ", TableKind.View));
        var view = Entity("Shop.Umsatz", null, Prop("Summe", "SUMME")) with { View = "V_UMSATZ" };
        var elsewhere = Entity("Shop.Fremd", "FREMD", Prop("Id", "ID")) with { Schema = "ANDERS" };

        var mapping = await ClrModelMapping.BuildAsync(Model(view, elsewhere), schema, Ct);

        Assert.Equal("Shop.Umsatz", mapping.EntityOf(new TableRef(Owner, "V_UMSATZ"))!.Entity.ClrType);
        Assert.Equal("Fremd: Tabelle ANDERS.FREMD gibt es nicht.", Assert.Single(mapping.Issues).Message);
    }

    [Fact]
    public async Task An_entity_on_a_view_is_matched_to_the_view_even_if_a_convention_gave_it_a_table_name()
    {
        // As in the user's project: ToView("GEP_MATBESTAND_VIEW") plus a naming convention that sets a table name for every
        // entity. EF queries the view; the table (which does not exist) would only serve SaveChanges.
        _columns["GEP_MATBESTAND_VIEW"] = ["MAT_ID", "BESTAND"];
        var schema = await SchemaAsync(new TableSummary(Owner, "GEP_MATBESTAND_VIEW", TableKind.View));
        var entity = Entity("Shop.GepMatbestandView", "GEPMATBESTANDVIEW",
                Prop("MatId", "MATID") with { ViewColumn = "MAT_ID" }, Prop("Bestand", "BESTAND") with { ViewColumn = "BESTAND" })
            with { View = "GEP_MATBESTAND_VIEW" };

        var mapping = await ClrModelMapping.BuildAsync(Model(entity), schema, Ct);

        var view = new TableRef(Owner, "GEP_MATBESTAND_VIEW");
        Assert.Equal("Shop.GepMatbestandView", mapping.EntityOf(view)!.Entity.ClrType);
        Assert.Equal("MatId", mapping.PropertyOf(view, "MAT_ID")!.Name); // the view's column name, not the table's
        Assert.Empty(mapping.Issues);
        Assert.Equal(1, mapping.MappedEntityCount);
    }

    [Fact]
    public async Task An_entity_on_a_table_and_a_view_maps_to_both_and_counts_once_a_missing_view_is_reported()
    {
        _columns["KUNDEN"] = ["ID"];
        _columns["V_KUNDEN"] = ["ID"];
        var schema = await SchemaAsync(new TableSummary(Owner, "KUNDEN", TableKind.Table), new TableSummary(Owner, "V_KUNDEN", TableKind.View));
        var both = Entity("Shop.Kunde", "KUNDEN", Prop("Id", "ID")) with { View = "V_KUNDEN" };
        var neither = Entity("Shop.Alt", "ALT", Prop("Id", "ID")) with { View = "V_ALT" };

        var mapping = await ClrModelMapping.BuildAsync(Model(both, neither), schema, Ct);

        Assert.NotNull(mapping.EntityOf(new TableRef(Owner, "KUNDEN")));
        Assert.NotNull(mapping.EntityOf(new TableRef(Owner, "V_KUNDEN")));
        Assert.Equal(1, mapping.MappedEntityCount);
        Assert.Equal("Alt: View V_ALT gibt es nicht.", Assert.Single(mapping.Issues).Message);
    }

    [Theory]
    [InlineData("Shop.Entities.Kunde", "Kunde")]
    [InlineData("Shop.Outer+Inner", "Inner")]
    [InlineData("System.Collections.Generic.Dictionary`2", "Dictionary")]
    public void Short_names_drop_the_namespace(string clrType, string expected) => Assert.Equal(expected, ClrModelMapping.ShortName(clrType));
}
