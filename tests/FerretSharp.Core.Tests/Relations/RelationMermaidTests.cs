using FerretSharp.Core.Relations;
using FerretSharp.Core.Schema;
using FerretSharp.Testing;

namespace FerretSharp.Core.Tests.Relations;

public class RelationMermaidTests
{
    private const string Owner = "APP";
    private static readonly TableRef Kunde = new(Owner, "KUNDE");
    private static readonly TableRef Auftrag = new(Owner, "AUFTRAG");
    private static readonly TableRef Land = new("STAMM", "LAND");
    private static readonly TableRef Mitarbeiter = new(Owner, "MITARBEITER");
    private static readonly TableRef Notiz = new(Owner, "NOTIZ$ALT");

    private static ForeignKeyInfo Fk(string name, TableRef from, TableRef to, FkSource source = FkSource.Declared) =>
        new(name, from, [to.Name + "_ID"], to, ["ID"], source);

    [Fact]
    public void Relationships_are_written_parent_first_dashed_without_constraint()
    {
        var graph = new RelationGraph([
            Fk("FK_AUFTRAG_KUNDE", Auftrag, Kunde),
            Fk("Kunde.Land", Kunde, Land, FkSource.ClrModel),
            Fk("FK_MA_VORGESETZTER", Mitarbeiter, Mitarbeiter),
            Fk("FK_AUFTRAG_MA", Auftrag, Mitarbeiter),
            Fk("FK_NOTIZ_KUNDE", Notiz, Kunde),
        ]);
        var diagram = new RelationDiagram(graph, Kunde, DiagramOptions.Default);

        var text = RelationMermaid.Write(diagram, Owner);

        Assert.Equal(
            """
            erDiagram
                KUNDE ||--o{ AUFTRAG : "FK_AUFTRAG_KUNDE"
                "STAMM.LAND" ||..o{ KUNDE : "Kunde.Land"
                KUNDE ||--o{ "NOTIZ$ALT" : "FK_NOTIZ_KUNDE"

            """.ReplaceLineEndings("\n"),
            text);
    }

    [Fact]
    public void Hidden_groups_are_named_in_a_comment_and_a_lone_table_still_appears()
    {
        var children = Enumerable.Range(1, 3).Select(i => new TableRef(Owner, $"KIND{i}")).ToList();
        var graph = new RelationGraph([.. children.Select(c => Fk($"FK_{c.Name}", c, Kunde))]);
        var diagram = new RelationDiagram(graph, Kunde, DiagramOptions.Default with { GroupLimit = 2 });

        using (UiCulture.Use("en"))
        {
            Assert.Equal("erDiagram\n    KUNDE\n    %% KUNDE: 3 more referencing tables not shown\n", RelationMermaid.Write(diagram, Owner));
        }
    }
}
