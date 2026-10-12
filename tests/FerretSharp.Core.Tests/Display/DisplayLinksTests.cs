using FerretSharp.Core.Display;
using FerretSharp.Core.Schema;
using NSubstitute;

namespace FerretSharp.Core.Tests.Display;

/// <summary>WP-31: which foreign key columns get the names of their target's display column.</summary>
public sealed class DisplayLinksTests
{
    private const string Owner = "APP";

    private static ColumnInfo Col(string name, string type, int? length = null) => new(name, type, length, false, null, null, true, false, null, 1);

    private static readonly TableDetails Kunde = new(new TableSummary(Owner, "KUNDE", TableKind.Table),
        [Col("ID", "NUMBER"), Col("NAME", "VARCHAR2", 100), Col("KUERZEL", "CHAR", 5), Col("BILD", "BLOB"), Col("PARENT_ID", "NUMBER"), Col("ORT", "VARCHAR2", 50)],
        ["ID"], [["KUERZEL"], ["NAME", "ORT"]], false);

    private static readonly TableDetails Land = new(new TableSummary(Owner, "SDLAND", TableKind.Table),
        [Col("ID", "NUMBER"), Col("BEZEICHNUNG", "VARCHAR2", 50)], ["ID"], [], false);

    private static readonly TableDetails KundeView = new(new TableSummary(Owner, "V_KUNDE", TableKind.View),
        [Col("ID", "NUMBER"), Col("NAME", "VARCHAR2", 100)], [], [], false);

    private static readonly TableDetails Auftrag = new(new TableSummary(Owner, "AUFTRAG", TableKind.Table),
        [
            Col("ID", "NUMBER"), Col("KUNDE_ID", "NUMBER"), Col("SDLAND_ID", "NUMBER"), Col("KUNDE_KZ", "CHAR", 5),
            Col("VERTRETER", "VARCHAR2", 10), Col("V_KUNDE_ID", "NUMBER"), Col("KUNDE_NAME", "VARCHAR2", 100), Col("KUNDE_ORT", "VARCHAR2", 50),
        ],
        ["ID"], [], false);

    private static ForeignKeyInfo Fk(string name, TableDetails from, string[] fromColumns, TableDetails to, string[] toColumns, FkSource source = FkSource.Declared) =>
        new(name, from.Table.Ref, fromColumns, to.Table.Ref, toColumns, source);

    private static readonly ForeignKeyInfo AuftragKunde = Fk("FK_AUFTRAG_KUNDE", Auftrag, ["KUNDE_ID"], Kunde, ["ID"]);

    private static readonly ForeignKeyInfo[] ForeignKeys =
    [
        AuftragKunde,
        Fk("Auftrag.Land", Auftrag, ["SDLAND_ID"], Land, ["ID"], FkSource.ClrModel),
        Fk("FK_AUFTRAG_KUNDE_KZ", Auftrag, ["KUNDE_KZ"], Kunde, ["KUERZEL"]),
        Fk("Auftrag.Vertreter", Auftrag, ["VERTRETER"], Kunde, ["ID"], FkSource.ClrModel), // VARCHAR2 → NUMBER
        Fk("Auftrag.VKunde", Auftrag, ["V_KUNDE_ID"], KundeView, ["ID"], FkSource.ClrModel),
        Fk("FK_AUFTRAG_KUNDE_NAME", Auftrag, ["KUNDE_NAME", "KUNDE_ORT"], Kunde, ["NAME", "ORT"]), // composite
        Fk("Auftrag.KundeSonst", Auftrag, ["KUNDE_ID"], Land, ["ID"], FkSource.ClrModel), // a second one on KUNDE_ID: the first wins
        Fk("FK_KUNDE_PARENT", Kunde, ["PARENT_ID"], Kunde, ["ID"]),
    ];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<SchemaCache> SchemaAsync()
    {
        var tables = new[] { Kunde, Land, KundeView, Auftrag };
        var reader = Substitute.For<ISchemaReader>();
        reader.GetTablesAsync(Owner, Arg.Any<CancellationToken>()).Returns(tables.Select(t => t.Table).ToList());
        reader.GetSynonymTargetsAsync(Owner, Arg.Any<CancellationToken>()).Returns(SynonymTargets.None);
        reader.GetForeignKeysAsync(Owner, Arg.Any<CancellationToken>()).Returns(ForeignKeys.Where(f => f.Source == FkSource.Declared).ToList());
        reader.GetDetailsAsync(Arg.Any<TableSummary>(), Arg.Any<CancellationToken>())
            .Returns(ci => tables.Single(t => t.Table == ci.Arg<TableSummary>()));
        var schema = new SchemaCache(reader, Owner);
        await schema.LoadAsync(Ct);
        schema.SetForeignKeys(FkSource.ClrModel, ForeignKeys.Where(f => f.Source == FkSource.ClrModel).ToList());
        return schema;
    }

    private static readonly DisplayColumns Columns = DisplayColumns.Empty
        .With("KUNDE", "NAME").With("SDLAND", "BEZEICHNUNG").With("V_KUNDE", "NAME").With("ARTIKEL", "BEZ");

    [Fact]
    public async Task Single_column_foreign_keys_to_a_unique_key_of_a_table_with_a_display_column_get_names()
    {
        var links = await DisplayLinks.PlanAsync(Auftrag, await SchemaAsync(), Columns, Ct);

        Assert.Equal(
            [("KUNDE_ID", "KUNDE", "ID", "NAME", false), ("KUNDE_KZ", "KUNDE", "KUERZEL", "NAME", true), ("SDLAND_ID", "SDLAND", "ID", "BEZEICHNUNG", false)],
            links.Select(l => (Auftrag.Columns[l.Column].Name, l.Target.Table.Name, l.Key.Name, l.Display.Name, l.SourcePadded)));
        Assert.Same(AuftragKunde, links[0].ForeignKey);
    }

    [Fact]
    public async Task A_table_referencing_itself_gets_names_too()
    {
        var link = Assert.Single(await DisplayLinks.PlanAsync(Kunde, await SchemaAsync(), Columns, Ct));

        Assert.Equal(("PARENT_ID", "NAME"), (Kunde.Columns[link.Column].Name, link.Display.Name));
    }

    [Fact]
    public async Task Without_display_columns_nothing_is_looked_up()
    {
        Assert.Empty(await DisplayLinks.PlanAsync(Auftrag, await SchemaAsync(), DisplayColumns.Empty, Ct));
    }

    [Theory]
    [InlineData("BILD")] // a LOB does not read as text
    [InlineData("GIBT_ES_NICHT")] // an entry for another database
    public void A_display_column_that_cannot_be_shown_gives_no_link(string column)
    {
        Assert.Null(DisplayLinks.Resolve(Auftrag, 1, AuftragKunde, Kunde, DisplayColumns.Empty.With("KUNDE", column)));
    }

    [Fact]
    public void An_nchar_key_gives_no_link()
    {
        var target = Kunde with { Columns = [Col("ID", "NUMBER"), Col("NAME", "VARCHAR2", 100), Col("KUERZEL", "NCHAR", 5)] };
        var source = Auftrag with { Columns = [.. Auftrag.Columns.Take(3), Col("KUNDE_KZ", "NCHAR", 5), .. Auftrag.Columns.Skip(4)] };
        var fk = Fk("FK_AUFTRAG_KUNDE_KZ", source, ["KUNDE_KZ"], target, ["KUERZEL"]);

        Assert.Null(DisplayLinks.Resolve(source, 3, fk, target, Columns));
    }

    [Fact]
    public void A_key_that_is_not_unique_gives_no_link()
    {
        var fk = Fk("Auftrag.Ort", Auftrag, ["KUNDE_ORT"], Kunde, ["ORT"], FkSource.ClrModel);

        Assert.Null(DisplayLinks.Resolve(Auftrag, 7, fk, Kunde, Columns));
    }
}
