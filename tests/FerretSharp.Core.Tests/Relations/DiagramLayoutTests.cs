using FerretSharp.Core.Relations;

namespace FerretSharp.Core.Tests.Relations;

public class DiagramLayoutTests
{
    private static readonly LayoutMetrics Metrics = new(NodeWidth: 100, NodeHeight: 40, LayerGap: 60, NodeGap: 10, Margin: 20);

    private static LayoutNode N(string id, int layer) => new(id, layer);

    private static LayoutEdge E(string from, string to) => new($"{from}>{to}", from, to);

    private static List<string> Column(DiagramPlacement placement, int layer) =>
        [.. placement.Nodes.Values.Where(b => b.Layer == layer).OrderBy(b => b.Y).Select(b => b.Id)];

    [Fact]
    public void Columns_follow_the_layers_from_left_to_right()
    {
        var placement = DiagramLayout.Arrange([N("a", 0), N("p", -1), N("c", 1)], [E("p", "a"), E("a", "c")], Metrics);

        Assert.Equal(20, placement.Nodes["p"].X);
        Assert.Equal(180, placement.Nodes["a"].X);
        Assert.Equal(340, placement.Nodes["c"].X);
        Assert.Equal(340 + 100 + 20, placement.Width);
    }

    [Fact]
    public void A_node_stands_level_with_the_middle_of_its_neighbours()
    {
        var placement = DiagramLayout.Arrange(
            [N("a", 0), N("c1", 1), N("c2", 1), N("c3", 1)], [E("a", "c1"), E("a", "c2"), E("a", "c3")], Metrics);

        Assert.Equal(placement.Nodes["c2"].CenterY, placement.Nodes["a"].CenterY);
        Assert.Equal(20, placement.Nodes["c1"].Y);
        Assert.Equal(70, placement.Nodes["c2"].Y);
        Assert.Equal(20 + (3 * 40) + (2 * 10) + 20, placement.Height);
    }

    [Fact]
    public void Ordering_untangles_crossed_edges()
    {
        // x1 → y2 and x2 → y1 cross in input order.
        var placement = DiagramLayout.Arrange(
            [N("x1", 0), N("x2", 0), N("y1", 1), N("y2", 1)], [E("x1", "y2"), E("x2", "y1")], Metrics);

        var left = Column(placement, 0);
        var right = Column(placement, 1);
        Assert.Equal(left.IndexOf("x1"), right.IndexOf("y2"));
        Assert.Equal(left.IndexOf("x2"), right.IndexOf("y1"));
    }

    [Fact]
    public void Growing_keeps_the_order_and_puts_new_nodes_beside_their_neighbours()
    {
        var nodes = new List<LayoutNode> { N("a", 0), N("c1", 1), N("c2", 1), N("c3", 1) };
        var edges = new List<LayoutEdge> { E("a", "c1"), E("a", "c2"), E("a", "c3") };
        var before = DiagramLayout.Arrange(nodes, edges, Metrics);
        var order = Column(before, 1);

        // A new parent of c3 goes into a new column left of a; a new sibling column right of c3 next to it.
        nodes.AddRange([N("p", -1), N("k1", 1)]);
        edges.AddRange([E("p", "a"), E("k1", "c3")]);
        var after = DiagramLayout.Arrange(nodes, edges, Metrics, before);

        Assert.Equal(order, Column(after, 1).Where(order.Contains));
        Assert.Equal("k1", Column(after, 1).Last());
        Assert.Equal(after.Nodes["a"].CenterY, after.Nodes["p"].CenterY);
    }

    [Fact]
    public void Several_edges_between_two_nodes_leave_side_by_side()
    {
        var placement = DiagramLayout.Arrange([N("p", 0), N("c", 1)], [new("r1", "p", "c"), new("r2", "p", "c")], Metrics);

        var starts = placement.Edges.Select(e => e.Points[0].Y).ToList();
        Assert.Equal(2, starts.Distinct().Count());
        Assert.Equal(placement.Nodes["p"].CenterY, starts.Average());
    }

    [Fact]
    public void An_edge_starts_at_its_from_node_whichever_side_it_stands()
    {
        var placement = DiagramLayout.Arrange([N("p", 1), N("c", 0)], [E("p", "c")], Metrics);
        var edge = Assert.Single(placement.Edges);

        Assert.Equal(placement.Nodes["p"].X, edge.Points[0].X);
        Assert.Equal(placement.Nodes["c"].Right, edge.Points[3].X);
        Assert.StartsWith("M 180 40 C", edge.Svg);
    }

    [Fact]
    public void An_edge_across_a_column_passes_the_nodes_there_in_a_lane_of_its_own()
    {
        // a → c skips the column of b, which stands level with both.
        var placement = DiagramLayout.Arrange([N("a", 0), N("b", 1), N("c", 2)], [E("a", "b"), E("b", "c"), E("a", "c")], Metrics);
        var b = placement.Nodes["b"];
        var across = placement.Edges.Single(e => e.Id == "a>c");

        Assert.Equal(10, across.Points.Count); // three curves: into the lane, through it, out of it
        Assert.Equal(placement.Nodes["a"].Right, across.Points[0].X);
        Assert.Equal(placement.Nodes["c"].X, across.Points[^1].X);
        var inLane = across.Points.Where(p => p.X >= b.X && p.X <= b.Right).ToList();
        Assert.NotEmpty(inLane);
        Assert.All(inLane, p => Assert.True(p.Y < b.Y || p.Y > b.Bottom));
        Assert.Equal(3, placement.Nodes.Count); // the lane is no node
        Assert.Equal(3, across.Svg.Split(" C ").Length - 1);
    }

    [Fact]
    public void Two_edges_with_the_same_id_across_columns_get_a_lane_each()
    {
        var placement = DiagramLayout.Arrange(
            [N("a", 0), N("b", 1), N("c", 2)], [E("a", "b"), new("same", "a", "c"), new("same", "a", "c")], Metrics);

        var across = placement.Edges.Where(e => e.Id == "same").ToList();
        Assert.Equal(2, across.Count);
        Assert.NotEqual(across[0].Points[4].Y, across[1].Points[4].Y);
    }

    [Fact]
    public void An_edge_across_columns_to_the_left_still_starts_at_its_from_node()
    {
        var placement = DiagramLayout.Arrange([N("p", 2), N("m", 1), N("c", 0)], [E("p", "m"), E("m", "c"), E("p", "c")], Metrics);
        var across = placement.Edges.Single(e => e.Id == "p>c");

        Assert.Equal(placement.Nodes["p"].X, across.Points[0].X);
        Assert.Equal(placement.Nodes["c"].Right, across.Points[^1].X);
    }

    [Fact]
    public void A_self_reference_loops_on_the_right_of_its_node()
    {
        var placement = DiagramLayout.Arrange([N("m", 0)], [E("m", "m")], Metrics);
        var loop = Assert.Single(placement.Edges);

        Assert.All(loop.Points, p => Assert.True(p.X >= placement.Nodes["m"].Right));
    }

    [Fact]
    public void An_empty_diagram_has_no_size()
    {
        var placement = DiagramLayout.Arrange([], [], Metrics);

        Assert.Empty(placement.Nodes);
        Assert.Equal(0, placement.Width);
    }
}
