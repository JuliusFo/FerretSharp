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

    private static readonly TableDetails Position = new(new TableSummary(Owner, "POSITION", TableKind.Table),
        [Col("ID", "NUMBER"), Col("AUFTRAG_ID", "NUMBER")], ["ID"], [], false);

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
        Fk("FK_POSITION_AUFTRAG", Position, ["AUFTRAG_ID"], Auftrag, ["ID"]),
    ];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <param name="unreadable">A table whose details cannot be read (a missing grant); null for none.</param>
    private static async Task<SchemaCache> SchemaAsync(TableDetails? unreadable = null)
    {
        var tables = new[] { Kunde, Land, KundeView, Auftrag, Position };
        var reader = Substitute.For<ISchemaReader>();
        reader.GetTablesAsync(Owner, Arg.Any<CancellationToken>()).Returns(tables.Select(t => t.Table).ToList());
        reader.GetSynonymTargetsAsync(Owner, Arg.Any<CancellationToken>()).Returns(SynonymTargets.None);
        reader.GetForeignKeysAsync(Arg.Is<IReadOnlyList<string>>(o => o.Contains(Owner)), Arg.Any<CancellationToken>()).Returns(ForeignKeys.Where(f => f.Source == FkSource.Declared).ToList());
        reader.GetDetailsAsync(Arg.Any<TableSummary>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<TableSummary>() == unreadable?.Table
                ? throw new Core.Connections.DatabaseException("ORA-01031: insufficient privileges", "ORA-01031")
                : tables.Single(t => t.Table == ci.Arg<TableSummary>()));
        var schema = new SchemaCache(reader, Owner);
        await schema.LoadAsync(Ct);
        schema.SetForeignKeys(FkSource.ClrModel, ForeignKeys.Where(f => f.Source == FkSource.ClrModel).ToList());
        return schema;
    }

    private static readonly DisplayColumns Columns = DisplayColumns.Empty
        .With("KUNDE", ["NAME"]).With("SDLAND", ["BEZEICHNUNG"]).With("V_KUNDE", ["NAME"]).With("ARTIKEL", ["BEZ"]);

    [Fact]
    public async Task Single_column_foreign_keys_to_a_unique_key_of_a_table_with_a_display_column_get_names()
    {
        var links = await DisplayLinks.PlanAsync(Auftrag, await SchemaAsync(), Columns, Ct);

        Assert.Equal(
            [("KUNDE_ID", "KUNDE", "ID", "NAME", false), ("KUNDE_KZ", "KUNDE", "KUERZEL", "NAME", true), ("SDLAND_ID", "SDLAND", "ID", "BEZEICHNUNG", false)],
            links.Select(l => (Auftrag.Columns[l.Column].Name, l.Target.Table.Name, l.Key.Name, l.Label, l.SourcePadded)));
        Assert.Same(AuftragKunde, links[0].ForeignKey);
    }

    [Fact]
    public async Task A_table_referencing_itself_gets_names_too()
    {
        var link = Assert.Single(await DisplayLinks.PlanAsync(Kunde, await SchemaAsync(), Columns, Ct));

        Assert.Equal(("PARENT_ID", "NAME"), (Kunde.Columns[link.Column].Name, link.Label));
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
        Assert.Null(DisplayLinks.Resolve(Auftrag, 1, AuftragKunde, Kunde, DisplayColumns.Empty.With("KUNDE", [column])));
    }

    /// <summary>R5b: an NCHAR key binds blank-padded since A18 – WP-31 left it out while it bound as NVARCHAR2.</summary>
    [Fact]
    public void An_nchar_key_gives_a_padded_link()
    {
        var target = Kunde with { Columns = [Col("ID", "NUMBER"), Col("NAME", "VARCHAR2", 100), Col("KUERZEL", "NCHAR", 5)] };
        var source = Auftrag with { Columns = [.. Auftrag.Columns.Take(3), Col("KUNDE_KZ", "NCHAR", 5), .. Auftrag.Columns.Skip(4)] };
        var fk = Fk("FK_AUFTRAG_KUNDE_KZ", source, ["KUNDE_KZ"], target, ["KUERZEL"]);

        Assert.True(DisplayLinks.Resolve(source, 3, fk, target, Columns)?.SourcePadded);
    }

    [Fact]
    public void A_key_that_is_not_unique_gives_no_link()
    {
        var fk = Fk("Auftrag.Ort", Auftrag, ["KUNDE_ORT"], Kunde, ["ORT"], FkSource.ClrModel);

        Assert.Null(DisplayLinks.Resolve(Auftrag, 7, fk, Kunde, Columns));
    }

    /// <summary>WP-65: several display columns in their order; those the target lacks or that do not read as text are left out.</summary>
    [Fact]
    public void A_name_takes_the_display_columns_in_order_that_the_target_has_and_can_show()
    {
        var columns = DisplayColumns.Empty.With("KUNDE", ["ORT", "BILD", "GIBT_ES_NICHT", "NAME"]);

        var link = DisplayLinks.Resolve(Auftrag, 1, AuftragKunde, Kunde, columns)!;

        Assert.Equal([5, 1], link.DisplayColumns);
        Assert.Equal("ORT - NAME", link.Label);
        Assert.Null(DisplayLinks.Resolve(Auftrag, 1, AuftragKunde, Kunde, DisplayColumns.Empty.With("KUNDE", ["BILD", "GIBT_ES_NICHT"])));
    }

    /// <summary>
    /// WP-65, stage 2: a display column that is a foreign key itself shows its target's name – one level deep, the nested
    /// link looks up nothing further even where it could (here KUNDE.PARENT_ID points back to KUNDE).
    /// </summary>
    [Fact]
    public async Task A_display_column_that_is_a_foreign_key_is_looked_up_one_level_deeper()
    {
        var columns = Columns.With("KUNDE", ["PARENT_ID", "NAME"]);

        var links = await DisplayLinks.PlanAsync(Auftrag, await SchemaAsync(), columns, Ct);

        var kunde = links.Single(l => l.Column == 1);
        Assert.Equal("PARENT_ID - NAME", kunde.Label);
        var via = kunde.Parts[0].Via!;
        Assert.Equal(("KUNDE", "ID", "PARENT_ID - NAME"), (via.Target.Table.Name, via.Key.Name, via.Label));
        Assert.All(via.Parts, p => Assert.Null(p.Via));
        Assert.Null(kunde.Parts[1].Via);
        Assert.All(links.Single(l => l.Column == 2).Parts, p => Assert.Null(p.Via)); // SDLAND: a plain name
    }

    [Fact]
    public async Task Plans_of_the_same_links_are_the_same_also_one_level_deeper()
    {
        var schema = await SchemaAsync();
        var columns = Columns.With("KUNDE", ["PARENT_ID", "NAME"]);

        var first = await DisplayLinks.PlanAsync(Auftrag, schema, columns, Ct);
        var second = await DisplayLinks.PlanAsync(Auftrag, schema, columns, Ct);
        var flat = await DisplayLinks.PlanAsync(Auftrag, schema, Columns.With("KUNDE", ["NAME", "PARENT_ID"]), Ct);

        Assert.All(first.Zip(second), p => Assert.True(p.First.SameAs(p.Second)));
        Assert.False(first[0].SameAs(flat[0]));
        Assert.False(first[0].SameAs(DisplayLinks.Nest(first[0] with { Parts = [.. first[0].Parts.Select(p => p with { Via = null })] }, new Dictionary<int, DisplayLink>())));
    }

    /// <summary>Review WP-65: other display columns one level deeper are another plan – the grid plans its names anew.</summary>
    [Fact]
    public async Task A_change_only_one_level_deeper_makes_another_plan()
    {
        var schema = await SchemaAsync();
        var auftrag = DisplayColumns.Empty.With("AUFTRAG", ["KUNDE_ID"]);

        var byName = Assert.Single(await DisplayLinks.PlanAsync(Position, schema, auftrag.With("KUNDE", ["NAME"]), Ct));
        var byPlace = Assert.Single(await DisplayLinks.PlanAsync(Position, schema, auftrag.With("KUNDE", ["ORT"]), Ct));

        Assert.Equal(byName.Label, byPlace.Label); // the same first level
        Assert.False(byName.SameAs(byPlace));
        Assert.True(byName.SameAs(Assert.Single(await DisplayLinks.PlanAsync(Position, schema, auftrag.With("KUNDE", ["NAME"]), Ct))));
    }

    /// <summary>Review WP-65: details one level deeper that cannot be read cost that level only, not the names of the first.</summary>
    [Fact]
    public async Task Details_one_level_deeper_that_cannot_be_read_leave_the_first_level()
    {
        var columns = DisplayColumns.Empty.With("AUFTRAG", ["KUNDE_ID"]).With("KUNDE", ["NAME"]);

        var failed = new List<Core.Connections.DatabaseException>();

        var link = Assert.Single(await DisplayLinks.PlanAsync(Position, await SchemaAsync(unreadable: Kunde), columns, Ct, failed.Add));

        Assert.Equal("ORA-01031", Assert.Single(failed).ErrorCode); // for the log: why the part shows its values
        Assert.Equal(("AUFTRAG", "KUNDE_ID"), (link.Target.Table.Name, link.Label));
        Assert.Null(Assert.Single(link.Parts).Via);
        Assert.NotNull(Assert.Single(Assert.Single(await DisplayLinks.PlanAsync(Position, await SchemaAsync(), columns, Ct)).Parts).Via);
    }
}
