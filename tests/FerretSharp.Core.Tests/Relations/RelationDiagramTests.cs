using FerretSharp.Core.Relations;
using FerretSharp.Core.Schema;

namespace FerretSharp.Core.Tests.Relations;

public class RelationDiagramTests
{
    private const string Owner = "APP";
    private static readonly TableRef Kunde = new(Owner, "KUNDE");
    private static readonly TableRef Land = new(Owner, "LAND");
    private static readonly TableRef Auftrag = new(Owner, "AUFTRAG");
    private static readonly TableRef Position = new(Owner, "POSITION");
    private static readonly TableRef Artikel = new(Owner, "ARTIKEL");
    private static readonly TableRef Mitarbeiter = new(Owner, "MITARBEITER");

    private static ForeignKeyInfo Fk(string name, TableRef from, TableRef to, FkSource source = FkSource.Declared) =>
        new(name, from, [to.Name + "_ID"], to, ["ID"], source);

    private static readonly ForeignKeyInfo KundeLand = Fk("FK_KUNDE_LAND", Kunde, Land);
    private static readonly ForeignKeyInfo AuftragKunde = Fk("FK_AUFTRAG_KUNDE", Auftrag, Kunde);
    private static readonly ForeignKeyInfo AuftragBearbeiter = Fk("FK_AUFTRAG_BEARBEITER", Auftrag, Mitarbeiter);
    private static readonly ForeignKeyInfo PositionAuftrag = Fk("FK_POSITION_AUFTRAG", Position, Auftrag);
    private static readonly ForeignKeyInfo PositionArtikel = Fk("Position.Artikel", Position, Artikel, FkSource.ClrModel);
    private static readonly ForeignKeyInfo Vorgesetzter = Fk("FK_MA_VORGESETZTER", Mitarbeiter, Mitarbeiter);

    private static readonly RelationGraph Graph = new([KundeLand, AuftragKunde, AuftragBearbeiter, PositionAuftrag, PositionArtikel, Vorgesetzter]);

    private static int LayerOf(RelationDiagram diagram, TableRef table) => diagram.Tables.Single(t => t.Table == table).Layer;

    [Fact]
    public void One_level_shows_parents_to_the_left_and_children_to_the_right()
    {
        var diagram = new RelationDiagram(Graph, Auftrag, DiagramOptions.Default);

        Assert.Equal([Auftrag, Kunde, Mitarbeiter, Position], diagram.Tables.Select(t => t.Table));
        Assert.Equal(0, LayerOf(diagram, Auftrag));
        Assert.Equal(-1, LayerOf(diagram, Kunde));
        Assert.Equal(-1, LayerOf(diagram, Mitarbeiter));
        Assert.Equal(1, LayerOf(diagram, Position));
        Assert.Equal([AuftragBearbeiter, AuftragKunde, Vorgesetzter, PositionAuftrag], diagram.Relations);
    }

    [Fact]
    public void Two_levels_follow_parents_of_parents_and_children_of_children_only()
    {
        var diagram = new RelationDiagram(Graph, Kunde, DiagramOptions.Default with { Depth = 2 });

        Assert.Equal(-1, LayerOf(diagram, Land));
        Assert.Equal(1, LayerOf(diagram, Auftrag));
        Assert.Equal(2, LayerOf(diagram, Position));
        Assert.False(diagram.IsShown(Mitarbeiter)); // a parent of a child
        Assert.Equal(1, diagram.HiddenCount(Auftrag, DiagramSide.Parents));
    }

    [Fact]
    public void Expanding_a_side_shows_its_hidden_neighbours_one_column_further_out()
    {
        var diagram = new RelationDiagram(Graph, Kunde, DiagramOptions.Default);
        Assert.Equal(1, diagram.HiddenCount(Auftrag, DiagramSide.Parents));
        Assert.Equal(1, diagram.HiddenCount(Auftrag, DiagramSide.Children));
        Assert.DoesNotContain(Vorgesetzter, diagram.Relations);

        Assert.Equal([Mitarbeiter], diagram.Expand(Auftrag, DiagramSide.Parents));

        Assert.Equal(0, LayerOf(diagram, Mitarbeiter));
        Assert.Equal(0, diagram.HiddenCount(Auftrag, DiagramSide.Parents));
        Assert.Empty(diagram.Expand(Auftrag, DiagramSide.Parents));
        Assert.Contains(Vorgesetzter, diagram.Relations); // the relationships asked for before expanding are not kept
        Assert.Same(AuftragBearbeiter, diagram.RelationOf(RelationGraph.KeyOf(AuftragBearbeiter)));
        Assert.Null(diagram.RelationOf("g:APP.KUNDE:Children"));
    }

    [Fact]
    public void Hidden_relations_lead_only_to_hidden_neighbours()
    {
        var diagram = new RelationDiagram(Graph, Auftrag, DiagramOptions.Default);

        Assert.Equal([PositionArtikel], diagram.HiddenRelations(Position, DiagramSide.Parents)); // not the one to AUFTRAG, shown
        Assert.Empty(diagram.HiddenRelations(Auftrag, DiagramSide.Parents));
        Assert.Empty(diagram.HiddenRelations(Mitarbeiter, DiagramSide.Parents)); // the self reference
        Assert.Equal([PositionAuftrag], new RelationDiagram(Graph, Kunde, DiagramOptions.Default).HiddenRelations(Auftrag, DiagramSide.Children));
    }

    [Fact]
    public void A_self_reference_is_drawn_but_counts_as_no_neighbour()
    {
        var diagram = new RelationDiagram(Graph, Mitarbeiter, DiagramOptions.Default);

        Assert.Equal([Mitarbeiter, Auftrag], diagram.Tables.Select(t => t.Table));
        Assert.Equal(0, diagram.HiddenCount(Mitarbeiter, DiagramSide.Parents));
        Assert.Contains(Vorgesetzter, diagram.Relations);
    }

    [Fact]
    public void Only_the_chosen_kinds_of_relationship_are_shown()
    {
        var declared = new RelationDiagram(Graph, Position, DiagramOptions.Default with { Sources = new HashSet<FkSource> { FkSource.Declared } });

        Assert.Equal([Position, Auftrag], declared.Tables.Select(t => t.Table));
        Assert.Equal(0, declared.HiddenCount(Position, DiagramSide.Parents));
    }

    [Fact]
    public void More_new_neighbours_than_the_limit_become_one_group_until_opened()
    {
        var children = Enumerable.Range(1, 5).Select(i => new TableRef(Owner, $"KIND{i}")).ToList();
        var graph = new RelationGraph([.. children.Select(c => Fk($"FK_{c.Name}", c, Land)), KundeLand]);
        var diagram = new RelationDiagram(graph, Land, DiagramOptions.Default with { GroupLimit = 4 });

        var group = Assert.Single(diagram.Groups);
        Assert.Equal((Land, DiagramSide.Children, 1), (group.Table, group.Side, group.Layer));
        Assert.Equal(6, diagram.Members(group).Count);
        Assert.Equal([Land], diagram.Tables.Select(t => t.Table));
        Assert.Equal(0, diagram.HiddenCount(Land, DiagramSide.Children));

        var shown = diagram.Open(group);

        Assert.Equal(6, shown.Count);
        Assert.Empty(diagram.Groups);
        Assert.All(shown, t => Assert.Equal(1, LayerOf(diagram, t)));
    }

    [Fact]
    public void A_group_member_shown_by_another_expansion_leaves_the_group()
    {
        var lieferant = new TableRef(Owner, "LIEFERANT");
        var others = new[] { new TableRef(Owner, "X1"), new TableRef(Owner, "X2") };
        var graph = new RelationGraph([
            Fk("FK_AUFTRAG_LAND", Auftrag, Land), PositionAuftrag, Fk("FK_POSITION_LIEFERANT", Position, lieferant),
            Fk("FK_LIEFERANT_LAND", lieferant, Land), .. others.Select(o => Fk($"FK_{o.Name}_LAND", o, Land)),
        ]);
        var diagram = new RelationDiagram(graph, Auftrag, DiagramOptions.Default with { GroupLimit = 2 });
        diagram.Expand(Land, DiagramSide.Children);
        var group = Assert.Single(diagram.Groups);
        Assert.Equal([lieferant, .. others], diagram.Members(group));

        Assert.Equal([lieferant], diagram.Expand(Position, DiagramSide.Parents));

        Assert.Equal(others, diagram.Members(group));
        Assert.Empty(diagram.Open(new DiagramGroup(Land, DiagramSide.Parents, -2, []))); // not a group of the diagram
        Assert.Equal(others, diagram.Open(group));
    }

    [Fact]
    public void The_layout_input_runs_edges_from_parent_to_child_and_hangs_groups_on_their_table()
    {
        var children = Enumerable.Range(1, 3).Select(i => new TableRef(Owner, $"KIND{i}")).ToList();
        var graph = new RelationGraph([.. children.Select(c => Fk($"FK_{c.Name}", c, Kunde)), KundeLand]);
        var diagram = new RelationDiagram(graph, Kunde, DiagramOptions.Default with { GroupLimit = 2 });

        var (nodes, edges) = diagram.ToLayout();

        Assert.Equal(["t:APP.KUNDE", "t:APP.LAND", "g:APP.KUNDE:Children"], nodes.Select(n => n.Id));
        Assert.Contains(edges, e => e.From == "t:APP.LAND" && e.To == "t:APP.KUNDE");
        Assert.Contains(edges, e => e.From == "t:APP.KUNDE" && e.To == "g:APP.KUNDE:Children");
    }
}
