using FerretSharp.Core.Query;
using FerretSharp.Core.Relations;
using FerretSharp.Core.Schema;
using FerretSharp.Testing;
using static FerretSharp.Core.Tests.Relations.MemoryTables;

namespace FerretSharp.Core.Tests.Relations;

public class QueryDesignTests
{
    private const string Owner = "APP";
    private static readonly TableRef Kunde = new(Owner, "KUNDE");
    private static readonly TableRef Auftrag = new(Owner, "AUFTRAG");
    private static readonly TableRef Position = new(Owner, "POSITION");
    private static readonly TableRef Artikel = new(Owner, "ARTIKEL");
    private static readonly TableRef Mitarbeiter = new(Owner, "MITARBEITER");

    private static ForeignKeyInfo Fk(string name, TableRef from, string column, TableRef to) => new(name, from, [column], to, ["ID"], FkSource.Declared);

    private static readonly ForeignKeyInfo Rechnungskunde = Fk("FK_AUFTRAG_RE_KUNDE", Auftrag, "RE_KUNDE_ID", Kunde);
    private static readonly ForeignKeyInfo Lieferkunde = Fk("FK_AUFTRAG_LI_KUNDE", Auftrag, "LI_KUNDE_ID", Kunde);
    private static readonly ForeignKeyInfo PositionAuftrag = Fk("FK_POSITION_AUFTRAG", Position, "AUFTRAG_ID", Auftrag);
    private static readonly ForeignKeyInfo PositionArtikel = Fk("FK_POSITION_ARTIKEL", Position, "ARTIKEL_ID", Artikel);
    private static readonly ForeignKeyInfo Vorgesetzter = Fk("FK_MA_VORGESETZTER", Mitarbeiter, "VORGESETZTER_ID", Mitarbeiter);

    private static readonly RelationGraph Graph = new([Rechnungskunde, Lieferkunde, PositionAuftrag, PositionArtikel, Vorgesetzter]);

    private static QueryStep Up(ForeignKeyInfo relation) => new(RelationGraph.KeyOf(relation), false);

    private static QueryStep Down(ForeignKeyInfo relation) => new(RelationGraph.KeyOf(relation), true);

    [Fact]
    public void A_node_is_a_path_the_same_table_can_come_in_twice_and_a_child_step_chooses_a_child()
    {
        var state = QueryDesignState.Empty
            .Add([], Up(Rechnungskunde))
            .Add([], Up(Lieferkunde))
            .Add([], Down(PositionAuftrag))
            .Add([Down(PositionAuftrag)], Up(PositionArtikel));

        var design = QueryDesign.Resolve(Auftrag, state, Graph);

        Assert.Equal([Auftrag, Kunde, Kunde, Position, Artikel], design.Nodes.Select(n => n.Table));
        Assert.Equal([0, 1, 2, 3, 4], design.Nodes.Select(n => n.Index));
        Assert.Equal([false, false, false, true, false], design.Nodes.Select(n => n.IsChosenChild));
        Assert.Same(design.Nodes[3], design.Nodes[4].From);
        Assert.Equal("AUFTRAG → POSITION → ARTIKEL", QueryDesign.Label(design.Nodes[4]));
        Assert.Empty(design.Notes);
        Assert.Empty(design.NextSteps(design.Nodes[3], Graph)); // the article is taken, the way back to the order is none
    }

    [Fact]
    public void A_self_reference_goes_either_way_and_a_click_on_it_leads_to_the_parent()
    {
        var state = QueryDesignState.Empty.Add([], Up(Vorgesetzter)).Add([], Down(Vorgesetzter));

        var design = QueryDesign.Resolve(Mitarbeiter, state, Graph);

        Assert.Equal([false, true], design.Nodes.Skip(1).Select(n => n.IsChosenChild));
        Assert.Equal(Up(Vorgesetzter), QueryDesign.StepFrom(Mitarbeiter, Vorgesetzter));
        Assert.Equal(Down(PositionAuftrag), QueryDesign.StepFrom(Auftrag, PositionAuftrag));
        Assert.Null(QueryDesign.StepFrom(Kunde, PositionAuftrag));
        Assert.Empty(design.NextSteps(design.Nodes[0], Graph));
    }

    [Fact]
    public void A_relationship_that_is_gone_takes_its_node_and_everything_beyond_it_out_with_a_note()
    {
        var state = QueryDesignState.Empty
            .Add([], Up(Rechnungskunde))
            .Add([], Down(PositionAuftrag))
            .Add([Down(PositionAuftrag)], Up(PositionArtikel));
        var withoutPositions = new RelationGraph([Rechnungskunde, PositionArtikel]);

        var design = QueryDesign.Resolve(Auftrag, state, withoutPositions);

        Assert.Equal([Auftrag, Kunde], design.Nodes.Select(n => n.Table));
        Assert.Equal(2, design.State.Nodes.Count);
        using (UiCulture.Use("en"))
        {
            Assert.Equal(["AUFTRAG: relationship FK_POSITION_AUFTRAG no longer exists – left out of the query."],
                QueryDesign.Resolve(Auftrag, state, withoutPositions).Notes);
        }
    }

    [Fact]
    public void Columns_its_tables_no_longer_have_go_out_of_columns_conditions_and_order_with_a_note_each()
    {
        var state = QueryDesignState.Empty
            .Update([], n => n with
            {
                Columns = [new QueryColumn("NR"), new QueryColumn("ALT")],
                Filters = [new QueryFilter(FilterCondition.Of("WEG", FilterOperator.Equals, "1"))],
            })
            .Add([], Down(PositionAuftrag))
            .Update([Down(PositionAuftrag)], n => n with { Sorts = [new SortSpec("ALT", false), new SortSpec("POS_NR", true)] });
        var auftrag = Table("AUFTRAG", ["ID"], Col("ID"), Col("NR"));
        var position = Table("POSITION", ["AUFTRAG_ID", "POS_NR"], Col("AUFTRAG_ID"), Col("POS_NR"));

        var design = QueryDesign.Resolve(Auftrag, state, Graph);
        var known = design.WithColumnsOf(t => t == Auftrag ? auftrag : null);
        var checkedBoth = design.WithColumnsOf(t => t == Auftrag ? auftrag : position);

        Assert.Equal(["NR"], known.Nodes[0].State.Columns.Select(c => c.Column));
        Assert.Empty(known.Nodes[0].State.Filters);
        Assert.Equal(2, known.Nodes[1].State.Sorts.Count); // details not known yet: untouched
        Assert.Equal([new SortSpec("POS_NR", true)], checkedBoth.Nodes[1].State.Sorts);
        Assert.Same(checkedBoth.Nodes[0], checkedBoth.Nodes[1].From);
        Assert.Equal(checkedBoth.Nodes.Select(n => n.State), checkedBoth.State.Nodes);
        Assert.Equal(3, checkedBoth.Notes.Count); // ALT and WEG at the root, ALT at the position
    }

    [Fact]
    public void Removing_a_node_removes_what_is_reached_through_it_but_never_the_root()
    {
        var state = QueryDesignState.Empty
            .Add([], Down(PositionAuftrag))
            .Add([Down(PositionAuftrag)], Up(PositionArtikel))
            .Add([], Up(Rechnungskunde));

        Assert.Equal(2, state.Remove([Down(PositionAuftrag)]).Nodes.Count);
        Assert.Same(state, state.Remove([]));
        Assert.Same(state, state.Add([Up(Lieferkunde)], Up(Rechnungskunde))); // from a node the query does not have
    }
}
