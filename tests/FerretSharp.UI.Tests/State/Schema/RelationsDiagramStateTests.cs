using FerretSharp.Core.Relations;
using FerretSharp.Core.Schema;
using FerretSharp.UI.State;

namespace FerretSharp.UI.Tests.State;

/// <summary>WP-46: the "Relations" view keeps what was expanded when the diagram is built again.</summary>
public sealed class RelationsDiagramStateTests
{
    private const string Owner = "APP";
    private static readonly TableRef Kunde = new(Owner, "KUNDE");
    private static readonly TableRef Auftrag = new(Owner, "AUFTRAG");
    private static readonly TableRef Position = new(Owner, "POSITION");
    private static readonly TableRef Artikel = new(Owner, "ARTIKEL");

    private static ForeignKeyInfo Fk(string name, TableRef from, TableRef to, FkSource source = FkSource.Declared) =>
        new(name, from, [to.Name + "_ID"], to, ["ID"], source);

    private static readonly RelationGraph Graph = new([
        Fk("FK_AUFTRAG_KUNDE", Auftrag, Kunde),
        Fk("FK_POSITION_AUFTRAG", Position, Auftrag),
        Fk("Position.Artikel", Position, Artikel, FkSource.ClrModel),
    ]);

    private static RelationsDiagramState Loaded()
    {
        var state = new RelationsDiagramState(Kunde);
        state.Load(Graph);
        return state;
    }

    private static List<TableRef> Shown(RelationsDiagramState state) => [.. state.Diagram!.Tables.Select(t => t.Table)];

    [Fact]
    public void Expanded_sides_are_replayed_when_the_schema_is_loaded_again()
    {
        var state = Loaded();
        state.Expand(Auftrag, DiagramSide.Children);
        state.Expand(Position, DiagramSide.Parents);
        Assert.Equal([Kunde, Auftrag, Position, Artikel], Shown(state));
        Assert.True(state.Changed);

        state.Load(Graph);

        Assert.Equal([Kunde, Auftrag, Position, Artikel], Shown(state));
        Assert.Equal(4, state.Placement!.Nodes.Count);
    }

    [Fact]
    public void A_kind_switched_off_hides_its_relationships_and_back_on_they_return()
    {
        var state = Loaded();
        state.Expand(Auftrag, DiagramSide.Children);
        state.Expand(Position, DiagramSide.Parents);
        Assert.Equal([FkSource.Declared, FkSource.ClrModel], state.Sources);

        state.ToggleSource(FkSource.ClrModel);
        Assert.False(state.Shows(FkSource.ClrModel));
        Assert.DoesNotContain(Artikel, Shown(state));

        state.ToggleSource(FkSource.ClrModel);
        Assert.Contains(Artikel, Shown(state));
    }

    [Fact]
    public void Putting_a_table_in_the_middle_starts_afresh_and_reset_goes_back_home()
    {
        var state = Loaded();
        state.Expand(Auftrag, DiagramSide.Children);
        state.SetDepth(2);

        state.Recenter(Position);
        Assert.Equal(Position, state.Focus);
        Assert.Equal(Position, state.Diagram!.Focus);
        Assert.Equal(2, state.Depth);

        state.Reset();
        Assert.Equal((Kunde, 1, false), (state.Focus, state.Depth, state.Changed));
        Assert.Equal([Kunde, Auftrag], Shown(state));
    }

    [Fact]
    public void An_opened_group_is_opened_again_after_a_reload()
    {
        var children = Enumerable.Range(1, 15).Select(i => new TableRef(Owner, $"KIND{i:00}")).ToList();
        var graph = new RelationGraph([.. children.Select(c => Fk($"FK_{c.Name}", c, Kunde))]);
        var state = new RelationsDiagramState(Kunde);
        state.Load(graph);
        var group = Assert.Single(state.Diagram!.Groups);

        state.Open(group);
        state.Load(graph);

        Assert.Empty(state.Diagram.Groups);
        Assert.Equal(16, state.Diagram.Tables.Count);
    }

    [Fact]
    public void Zoom_goes_in_steps_and_stops_at_both_ends()
    {
        var state = new RelationsDiagramState(Kunde);
        state.ZoomIn();
        Assert.Equal(1.1, state.Zoom);
        for (var i = 0; i < 20; i++)
        {
            state.ZoomOut();
        }

        Assert.Equal(0.5, state.Zoom);
        Assert.False(state.CanZoomOut);
        state.ResetZoom();
        Assert.Equal(1, state.Zoom);
    }
}
