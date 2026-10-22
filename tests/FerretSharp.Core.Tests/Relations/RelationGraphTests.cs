using FerretSharp.Core.Relations;
using FerretSharp.Core.Schema;

namespace FerretSharp.Core.Tests.Relations;

public class RelationGraphTests
{
    private const string Owner = "APP";
    private static readonly TableRef Kunde = new(Owner, "KUNDE");
    private static readonly TableRef Auftrag = new(Owner, "AUFTRAG");
    private static readonly TableRef Position = new(Owner, "POSITION");
    private static readonly TableRef Artikel = new(Owner, "ARTIKEL");
    private static readonly TableRef Rechnung = new(Owner, "RECHNUNG");
    private static readonly TableRef Mitarbeiter = new(Owner, "MITARBEITER");

    private static ForeignKeyInfo Fk(string name, TableRef from, TableRef to, FkSource source = FkSource.Declared) =>
        new(name, from, [to.Name + "_ID"], to, ["ID"], source);

    private static readonly ForeignKeyInfo AuftragKunde = Fk("FK_AUFTRAG_KUNDE", Auftrag, Kunde);
    private static readonly ForeignKeyInfo PositionAuftrag = Fk("FK_POSITION_AUFTRAG", Position, Auftrag);
    private static readonly ForeignKeyInfo PositionArtikel = Fk("Position.Artikel", Position, Artikel, FkSource.ClrModel);
    private static readonly ForeignKeyInfo Vorgesetzter = Fk("FK_MA_VORGESETZTER", Mitarbeiter, Mitarbeiter);

    [Fact]
    public void Parents_and_children_follow_the_direction_of_the_reference()
    {
        var graph = new RelationGraph([PositionAuftrag, AuftragKunde, PositionArtikel, Vorgesetzter]);

        Assert.Equal([PositionAuftrag, PositionArtikel], graph.ParentsOf(Position).OrderBy(r => r.Name));
        Assert.Equal([AuftragKunde], graph.ChildrenOf(Kunde));
        Assert.Equal([Vorgesetzter], graph.ParentsOf(Mitarbeiter));
        Assert.Equal([Vorgesetzter], graph.ChildrenOf(Mitarbeiter));
        Assert.Empty(graph.ChildrenOf(Position));
    }

    [Fact]
    public void A_relationship_is_found_again_by_its_key_after_the_schema_was_reloaded()
    {
        var graph = new RelationGraph([AuftragKunde, PositionArtikel]);
        var reloaded = new ForeignKeyInfo("Position.Artikel", Position, ["ARTIKEL_ID"], Artikel, ["ID"], FkSource.ClrModel);

        Assert.Same(PositionArtikel, graph.Find(RelationGraph.KeyOf(reloaded)));
        Assert.Null(graph.Find(RelationGraph.KeyOf(reloaded with { Source = FkSource.Manual })));
    }

    [Fact]
    public void Tables_are_ordered_parents_first_and_alphabetically_where_nothing_binds_them()
    {
        var order = RelationGraph.Order([Position, Auftrag, Kunde, Artikel], [PositionAuftrag, AuftragKunde, PositionArtikel]);

        Assert.False(order.HasCycles);
        Assert.Equal([Artikel, Kunde, Auftrag, Position], order.Tables);
    }

    [Fact]
    public void Relationships_to_tables_outside_the_set_and_self_references_do_not_bind_the_order()
    {
        var order = RelationGraph.Order([Position, Mitarbeiter], [PositionAuftrag, PositionArtikel, Vorgesetzter]);

        Assert.False(order.HasCycles);
        Assert.Equal([Mitarbeiter, Position], order.Tables);
    }

    [Fact]
    public void A_cycle_is_reported_with_its_relationships_and_its_tables_come_last()
    {
        var auftragRechnung = Fk("FK_AUFTRAG_RECHNUNG", Auftrag, Rechnung);
        var rechnungAuftrag = Fk("FK_RECHNUNG_AUFTRAG", Rechnung, Auftrag);

        var order = RelationGraph.Order(
            [Position, Auftrag, Kunde, Rechnung],
            [PositionAuftrag, AuftragKunde, auftragRechnung, rechnungAuftrag]);

        Assert.True(order.HasCycles);
        var cycle = Assert.Single(order.Cycles);
        Assert.Equal([Auftrag, Rechnung], cycle.Tables);
        Assert.Equal([auftragRechnung, rechnungAuftrag], cycle.Relations);
        Assert.Equal("AUFTRAG, RECHNUNG", cycle.Display);
        // KUNDE is free; POSITION waits below the cycle and comes with it at the end.
        Assert.Equal([Kunde, Auftrag, Position, Rechnung], order.Tables);
    }

    [Fact]
    public void Two_relationships_between_the_same_tables_in_one_direction_are_no_cycle()
    {
        var bestellt = Fk("FK_AUFTRAG_BESTELLER", Auftrag, Kunde);
        var liefert = Fk("FK_AUFTRAG_EMPFAENGER", Auftrag, Kunde);

        var order = RelationGraph.Order([Auftrag, Kunde], [bestellt, liefert]);

        Assert.False(order.HasCycles);
        Assert.Equal([Kunde, Auftrag], order.Tables);
    }
}
