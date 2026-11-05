using FerretSharp.Core.Relations;
using FerretSharp.Core.Schema;

namespace FerretSharp.Core.Tests.Relations;

public class PathDiagramTests
{
    private const string Owner = "APP";
    private static readonly TableRef Position = new(Owner, "POSITION");
    private static readonly TableRef Artikel = new(Owner, "ARTIKEL");
    private static readonly TableRef Lieferant = new(Owner, "LIEFERANT");
    private static readonly TableRef ArtikelLieferant = new(Owner, "ARTIKEL_LIEFERANT");

    private static ForeignKeyInfo Fk(string name, TableRef from, TableRef to, FkSource source = FkSource.Declared) =>
        new(name, from, [to.Name + "_ID"], to, ["ID"], source);

    private static readonly ForeignKeyInfo PositionArtikel = Fk("FK_POSITION_ARTIKEL", Position, Artikel);
    private static readonly ForeignKeyInfo ArtikelHersteller = Fk("Artikel.Hersteller", Artikel, Lieferant, FkSource.ClrModel);
    private static readonly ForeignKeyInfo AlArtikel = Fk("FK_AL_ARTIKEL", ArtikelLieferant, Artikel);
    private static readonly ForeignKeyInfo AlLieferant = Fk("FK_AL_LIEFERANT", ArtikelLieferant, Lieferant);

    private static readonly IReadOnlyList<RelationPath> Paths =
        new RelationPathSearch(new RelationGraph([PositionArtikel, ArtikelHersteller, AlArtikel, AlLieferant]), Position, Lieferant).Next(10, CancellationToken.None);

    [Fact]
    public void Start_left_target_right_and_each_table_at_its_shortest_distance()
    {
        var diagram = new PathDiagram(Position, Lieferant, Paths);

        Assert.Equal([(Position, 0), (Artikel, 1), (ArtikelLieferant, 2), (Lieferant, 3)], diagram.Tables.Select(t => (t.Table, t.Layer)));
        Assert.Equal(4, diagram.Relations.Count);

        var (nodes, edges) = diagram.ToLayout();
        Assert.Equal(4, nodes.Count);
        // Parent to child as in the relationship diagram: the dot stays at the referencing end.
        var edge = edges.Single(e => e.Id == RelationGraph.KeyOf(ArtikelHersteller));
        Assert.Equal((RelationDiagram.IdOf(Lieferant), RelationDiagram.IdOf(Artikel)), (edge.From, edge.To));
        Assert.Same(ArtikelHersteller, diagram.RelationOf(edge.Id));
        DiagramLayout.Arrange(nodes, edges, LayoutMetrics.Default); // a parent right of its child lays out
    }

    [Fact]
    public void Without_ways_shows_start_and_target()
    {
        var diagram = new PathDiagram(Position, Lieferant, []);

        Assert.Equal([(Position, 0), (Lieferant, 1)], diagram.Tables.Select(t => (t.Table, t.Layer)));
    }

    [Fact]
    public void Mermaid_has_each_relationship_of_the_ways_once()
    {
        var text = RelationMermaid.Write(Paths, t => t.Name);

        Assert.Equal(
            """
            erDiagram
                ARTIKEL ||--o{ POSITION : "FK_POSITION_ARTIKEL"
                LIEFERANT ||..o{ ARTIKEL : "Artikel.Hersteller"
                ARTIKEL ||--o{ ARTIKEL_LIEFERANT : "FK_AL_ARTIKEL"
                LIEFERANT ||--o{ ARTIKEL_LIEFERANT : "FK_AL_LIEFERANT"

            """.ReplaceLineEndings("\n"),
            text);
    }
}
