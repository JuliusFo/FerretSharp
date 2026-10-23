using System.Globalization;

namespace FerretSharp.Core.Relations;

/// <summary>
/// A layered layout for diagrams of relationships (WP-46, ADR 0033): nodes stand in columns by their layer – parents to
/// the left, children to the right –, all of one size. It knows nothing of tables, so the record diagram (WP-60) lays
/// out rows with it too. Growing a diagram keeps its nodes in their order: pass the placement before (see
/// <see cref="Arrange"/>), and new nodes are put in beside their neighbours instead of everything being sorted afresh.
/// </summary>
public static class DiagramLayout
{
    /// <summary>Ordering passes of a first layout, alternating to the right and to the left.</summary>
    private const int Sweeps = 4;

    /// <summary>Vertical distance between the ends of several edges between the same two nodes.</summary>
    private const double PortSpacing = 8;

    /// <param name="previous">The placement before the diagram grew: nodes found in it keep their order in their column.</param>
    public static DiagramPlacement Arrange(
        IReadOnlyList<LayoutNode> nodes, IReadOnlyList<LayoutEdge> edges, LayoutMetrics metrics, DiagramPlacement? previous = null)
    {
        if (nodes.Count == 0)
        {
            return new DiagramPlacement(new Dictionary<string, NodeBox>(), [], 0, 0);
        }

        var byId = nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
        var neighbours = nodes.ToDictionary(n => n.Id, _ => new List<string>(), StringComparer.Ordinal);
        foreach (var edge in edges.Where(e => e.From != e.To && byId.ContainsKey(e.From) && byId.ContainsKey(e.To)))
        {
            neighbours[edge.From].Add(edge.To);
            neighbours[edge.To].Add(edge.From);
        }

        var layers = nodes.GroupBy(n => n.Layer).OrderBy(g => g.Key).ToDictionary(g => g.Key, g => g.Select(n => n.Id).ToList());
        if (previous is not null && nodes.Any(n => previous.Nodes.ContainsKey(n.Id)))
        {
            OrderAgainst(layers, neighbours, previous);
        }
        else
        {
            OrderBySweeps(layers, neighbours);
        }

        var tops = PlaceVertically(layers, neighbours, metrics);
        var minLayer = layers.Keys.Min();
        var boxes = new Dictionary<string, NodeBox>(StringComparer.Ordinal);
        foreach (var (layer, ids) in layers)
        {
            for (var i = 0; i < ids.Count; i++)
            {
                var x = metrics.Margin + ((layer - minLayer) * (metrics.NodeWidth + metrics.LayerGap));
                boxes[ids[i]] = new NodeBox(ids[i], layer, i, x, tops[ids[i]], metrics.NodeWidth, metrics.NodeHeight);
            }
        }

        var width = boxes.Values.Max(b => b.Right) + metrics.Margin;
        var height = boxes.Values.Max(b => b.Bottom) + metrics.Margin;
        return new DiagramPlacement(boxes, Route(edges, boxes, metrics), width, height);
    }

    /// <summary>A first layout: the barycentre heuristic, each node towards the middle of its neighbours in the column before.</summary>
    private static void OrderBySweeps(Dictionary<int, List<string>> layers, Dictionary<string, List<string>> neighbours)
    {
        var keys = layers.Keys.Order().ToList();
        for (var sweep = 0; sweep < Sweeps; sweep++)
        {
            var forward = sweep % 2 == 0;
            var sequence = forward ? keys.Skip(1) : keys.AsEnumerable().Reverse().Skip(1);
            foreach (var layer in sequence)
            {
                var beside = layer + (forward ? -1 : 1);
                if (!layers.TryGetValue(beside, out var fixedIds))
                {
                    continue;
                }

                var fixedAt = Positions(fixedIds);
                var own = layers[layer];
                var ownAt = Positions(own);
                layers[layer] = [.. own.OrderBy(id => Barycentre(neighbours[id], fixedAt) ?? ownAt[id])];
            }
        }
    }

    /// <summary>
    /// Nodes of the placement before keep their order (by their place then); a new one goes where the middle of its
    /// placed neighbours is, otherwise to the end of its column.
    /// </summary>
    private static void OrderAgainst(Dictionary<int, List<string>> layers, Dictionary<string, List<string>> neighbours, DiagramPlacement previous)
    {
        foreach (var layer in layers.Keys.ToList())
        {
            layers[layer] = [.. layers[layer].OrderBy(id => previous.Nodes.TryGetValue(id, out var box)
                ? box.CenterY
                : neighbours[id].Where(previous.Nodes.ContainsKey).Select(n => previous.Nodes[n].CenterY).DefaultIfEmpty(double.MaxValue).Average())];
        }
    }

    /// <summary>
    /// Columns from the tallest outwards: a node is placed level with the middle of its neighbours in the columns placed
    /// already, as far as the nodes above it allow; the column is then moved up as a whole by its mean distance from where
    /// its nodes wanted to be. The tallest column is simply stacked.
    /// </summary>
    private static Dictionary<string, double> PlaceVertically(
        Dictionary<int, List<string>> layers, Dictionary<string, List<string>> neighbours, LayoutMetrics metrics)
    {
        var tops = new Dictionary<string, double>(StringComparer.Ordinal);
        var tallest = layers.OrderByDescending(l => l.Value.Count).ThenBy(l => Math.Abs(l.Key)).First().Key;
        var sequence = layers.Keys.OrderBy(l => Math.Abs(l - tallest)).ThenBy(l => l);
        var step = metrics.NodeHeight + metrics.NodeGap;
        foreach (var layer in sequence)
        {
            var ids = layers[layer];
            var wanted = ids.Select(id => neighbours[id].Where(tops.ContainsKey).Select(n => tops[n]).DefaultIfEmpty(double.NaN).Average()).ToList();
            var placed = new double[ids.Count];
            var next = metrics.Margin;
            for (var i = 0; i < ids.Count; i++)
            {
                placed[i] = double.IsNaN(wanted[i]) ? next : Math.Max(next, wanted[i]);
                next = placed[i] + step;
            }

            // Pushed down by the nodes above, the column moves back up as far as its nodes want on average, not above the margin.
            var off = ids.Select((_, i) => wanted[i] - placed[i]).Where(d => !double.IsNaN(d)).DefaultIfEmpty(0).Average();
            var shift = Math.Max(metrics.Margin - placed.DefaultIfEmpty(metrics.Margin).Min(), Math.Min(0, off));
            for (var i = 0; i < ids.Count; i++)
            {
                tops[ids[i]] = placed[i] + shift;
            }
        }

        // A column may have been moved up less than another wanted: start all at the margin.
        var top = tops.Values.Min();
        foreach (var id in tops.Keys.ToList())
        {
            tops[id] += metrics.Margin - top;
        }

        return tops;
    }

    /// <summary>
    /// A cubic curve per edge. Between columns from the right side of the left node to the left side of the right one;
    /// within a column in an arc on the right; a self reference as a loop on the right. Several edges between the same two
    /// nodes leave and arrive side by side.
    /// </summary>
    private static List<EdgePath> Route(IReadOnlyList<LayoutEdge> edges, Dictionary<string, NodeBox> boxes, LayoutMetrics metrics)
    {
        var paths = new List<EdgePath>();
        var drawn = edges.Where(e => boxes.ContainsKey(e.From) && boxes.ContainsKey(e.To)).ToList();
        foreach (var pair in drawn.GroupBy(e => string.CompareOrdinal(e.From, e.To) <= 0 ? (e.From, e.To) : (e.To, e.From)))
        {
            var list = pair.ToList();
            for (var i = 0; i < list.Count; i++)
            {
                var edge = list[i];
                var port = (i - ((list.Count - 1) / 2.0)) * PortSpacing;
                var (a, b) = (boxes[edge.From], boxes[edge.To]);
                paths.Add(new EdgePath(edge.Id, edge.From, edge.To, Curve(a, b, port, metrics)));
            }
        }

        return paths;
    }

    private static PathPoint[] Curve(NodeBox from, NodeBox to, double port, LayoutMetrics metrics)
    {
        if (from.Id == to.Id)
        {
            var x = from.Right;
            var y = from.CenterY;
            var loop = metrics.LayerGap / 3;
            return [new(x, y - 8 + port), new(x + loop, y - loop + port), new(x + loop, y + loop + port), new(x, y + 8 + port)];
        }

        if (from.Layer == to.Layer)
        {
            var bulge = metrics.LayerGap / 3 + Math.Abs(from.Order - to.Order) * 4;
            var x = from.Right;
            return [new(x, from.CenterY + port), new(x + bulge, from.CenterY + port), new(x + bulge, to.CenterY + port), new(x, to.CenterY + port)];
        }

        var (left, right, sign) = from.Layer < to.Layer ? (from, to, 1) : (to, from, -1);
        var start = new PathPoint(left.Right, left.CenterY + port);
        var end = new PathPoint(right.X, right.CenterY + port);
        var pull = Math.Max(metrics.LayerGap / 2, (end.X - start.X) / 3);
        PathPoint[] points = [start, new(start.X + pull, start.Y), new(end.X - pull, end.Y), end];
        return sign > 0 ? points : [.. points.Reverse()];
    }

    private static Dictionary<string, double> Positions(List<string> ids) =>
        ids.Select((id, i) => (id, i)).ToDictionary(p => p.id, p => (p.i + 0.5) / ids.Count, StringComparer.Ordinal);

    private static double? Barycentre(List<string> neighbours, Dictionary<string, double> at)
    {
        var placed = neighbours.Where(at.ContainsKey).Select(n => at[n]).ToList();
        return placed.Count == 0 ? null : placed.Average();
    }
}

/// <summary>A node to lay out; <paramref name="Layer"/> is its column (negative to the left of the start, positive to the right).</summary>
public sealed record LayoutNode(string Id, int Layer);

/// <summary>An edge between two nodes; it is drawn from <paramref name="From"/> to <paramref name="To"/>.</summary>
public sealed record LayoutEdge(string Id, string From, string To);

/// <summary>Sizes of the layout in CSS pixels.</summary>
public sealed record LayoutMetrics(double NodeWidth = 220, double NodeHeight = 48, double LayerGap = 120, double NodeGap = 14, double Margin = 24)
{
    public static readonly LayoutMetrics Default = new();
}

/// <summary>Where a node stands: its column, its place in the column (from the top) and its box.</summary>
public sealed record NodeBox(string Id, int Layer, int Order, double X, double Y, double Width, double Height)
{
    public double Right => X + Width;

    public double Bottom => Y + Height;

    public double CenterY => Y + (Height / 2);
}

public readonly record struct PathPoint(double X, double Y);

/// <summary>A drawn edge: a cubic Bézier curve from <see cref="From"/> to <see cref="To"/> through two control points.</summary>
public sealed record EdgePath(string Id, string From, string To, IReadOnlyList<PathPoint> Points)
{
    /// <summary>The curve as SVG path data ("M x y C …").</summary>
    public string Svg => string.Create(CultureInfo.InvariantCulture,
        $"M {Points[0].X:0.#} {Points[0].Y:0.#} C {Points[1].X:0.#} {Points[1].Y:0.#}, {Points[2].X:0.#} {Points[2].Y:0.#}, {Points[3].X:0.#} {Points[3].Y:0.#}");
}

/// <summary>The result of <see cref="DiagramLayout.Arrange"/>: boxes by node id, edge curves and the size of the whole.</summary>
public sealed record DiagramPlacement(IReadOnlyDictionary<string, NodeBox> Nodes, IReadOnlyList<EdgePath> Edges, double Width, double Height);
