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

    /// <summary>As <c>SchemaCache.LabelOf</c> without synonyms; LAND is known by the synonym LAENDER.</summary>
    private static string Label(TableRef table) => table == Land ? "LAENDER" : table.Owner == Owner ? table.Name : table.ToString();

    private static ForeignKeyInfo Fk(string name, TableRef from, TableRef to, FkSource source = FkSource.Declared) =>
        new(name, from, [to.Name + "_ID"], to, ["ID"], source);

    [Fact]
    public void Relationships_are_written_parent_first_dashed_without_constraint_with_the_labels_of_the_view()
    {
        var graph = new RelationGraph([
            Fk("FK_AUFTRAG_KUNDE", Auftrag, Kunde),
            Fk("Kunde.Land", Kunde, Land, FkSource.ClrModel),
            Fk("FK_MA_VORGESETZTER", Mitarbeiter, Mitarbeiter),
            Fk("FK_AUFTRAG_MA", Auftrag, Mitarbeiter),
            Fk("FK_NOTIZ_KUNDE", Notiz, Kunde),
        ]);
        var diagram = new RelationDiagram(graph, Kunde, DiagramOptions.Default);

        var text = RelationMermaid.Write(diagram, Label);

        Assert.Equal(
            """
            erDiagram
                KUNDE ||--o{ AUFTRAG : "FK_AUFTRAG_KUNDE"
                LAENDER ||..o{ KUNDE : "Kunde.Land"
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
            Assert.Equal("erDiagram\n    KUNDE\n    %% KUNDE: 3 more referencing tables not shown\n", RelationMermaid.Write(diagram, Label));
        }
    }

    /// <summary>
    /// Review 3.54.0, A10: a quoted Oracle name may hold a line break – it ended the label or comment, and the rest became
    /// lines of their own; <c>%%</c> starts a comment or directive anywhere.
    /// </summary>
    [Fact]
    public void Line_breaks_and_comment_marks_of_names_stay_inside_their_line()
    {
        var odd = new TableRef(Owner, "X\nY %%{init}%%");
        var graph = new RelationGraph([Fk("FK\r\nB", odd, Kunde)]);

        var text = RelationMermaid.Write(new RelationDiagram(graph, Kunde, DiagramOptions.Default), Label);

        Assert.Equal("erDiagram\n    KUNDE ||--o{ \"X Y % %{init}% %\" : \"FK B\"\n", text);
        Assert.Equal("a b % % % %", MermaidText.Plain("a\r\nb %%%%"));
    }
}
