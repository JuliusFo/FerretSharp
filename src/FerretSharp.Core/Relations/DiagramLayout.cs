using System.Globalization;
using System.Text;

namespace FerretSharp.Core.Relations;

/// <summary>
/// A layered layout for diagrams of relationships (WP-46, ADR 0033): nodes stand in columns by their layer – parents to
/// the left, children to the right –, all of one size. It knows nothing of tables, so the record diagram (WP-60) lays
/// out rows with it too. Growing a diagram keeps its nodes in their order: pass the placement before (see
/// <see cref="Arrange"/>), and new nodes are put in beside their neighbours instead of everything being sorted afresh.
/// An edge across several columns gets a lane in each column between (a waypoint ordered like a node), so it passes the
/// nodes there instead of running through them.
/// </summary>
public static class DiagramLayout
{
    /// <summary>Ordering passes of a first layout, alternating to the right and to the left.</summary>
    private const int Sweeps = 4;

    /// <summary>Vertical distance between the ends of several edges between the same two nodes.</summary>
    private const double PortSpacing = 8;

    /// <summary>Starts the id of a waypoint; node ids of callers do not contain it.</summary>
    private const char WaypointMark = '\u0001';

    /// <param name="previous">The placement before the diagram grew: nodes found in it keep their order in their column.</param>
    public static DiagramPlacement Arrange(
        IReadOnlyList<LayoutNode> nodes, IReadOnlyList<LayoutEdge> edges, LayoutMetrics metrics, DiagramPlacement? previous = null)
    {
        if (nodes.Count == 0)
        {
            return new DiagramPlacement(new Dictionary<string, NodeBox>(), [], 0, 0);
        }

        var layerOf = nodes.ToDictionary(n => n.Id, n => n.Layer, StringComparer.Ordinal);
        var neighbours = nodes.ToDictionary(n => n.Id, _ => new List<string>(), StringComparer.Ordinal);
        var columns = nodes.Select(n => (n.Id, n.Layer)).ToList();
        // By the edge's place in the list, not its id: two edges may share an id, each needs lanes of its own (review).
        var chains = new List<string>?[edges.Count];
        for (var index = 0; index < edges.Count; index++)
        {
            var edge = edges[index];
            if (edge.From == edge.To || !layerOf.ContainsKey(edge.From) || !layerOf.ContainsKey(edge.To))
            {
                continue;
            }

            var chain = new List<string> { edge.From };
            var (from, to) = (layerOf[edge.From], layerOf[edge.To]);
            var step = Math.Sign(to - from);
            for (var layer = from + step; step != 0 && layer != to; layer += step)
            {
                var waypoint = $"{WaypointMark}{index}{WaypointMark}{layer}";
                neighbours[waypoint] = [];
                columns.Add((waypoint, layer));
                chain.Add(waypoint);
            }

            chain.Add(edge.To);
            for (var i = 1; i < chain.Count; i++)
            {
                neighbours[chain[i - 1]].Add(chain[i]);
                neighbours[chain[i]].Add(chain[i - 1]);
            }

            chains[index] = chain;
        }

        var layers = columns.GroupBy(c => c.Layer).OrderBy(g => g.Key).ToDictionary(g => g.Key, g => g.Select(c => c.Id).ToList());
        if (previous is not null && nodes.Any(n => previous.Nodes.ContainsKey(n.Id)))
        {
            OrderAgainst(layers, neighbours, previous);
        }
        else
        {
            OrderBySweeps(layers, neighbours);
        }

        // A waypoint is a lane as high as the gap between nodes, so its edge keeps that distance from them.
        double HeightOf(string id) => IsWaypoint(id) ? metrics.NodeGap : metrics.NodeHeight;
        var tops = PlaceVertically(layers, neighbours, HeightOf, metrics);
        var minLayer = layers.Keys.Min();
        var boxes = new Dictionary<string, NodeBox>(StringComparer.Ordinal);
        foreach (var (layer, ids) in layers)
        {
            var x = metrics.Margin + ((layer - minLayer) * (metrics.NodeWidth + metrics.LayerGap));
            for (var i = 0; i < ids.Count; i++)
            {
                boxes[ids[i]] = new NodeBox(ids[i], layer, i, x, tops[ids[i]], metrics.NodeWidth, HeightOf(ids[i]));
            }
        }

        var width = boxes.Values.Max(b => b.Right) + metrics.Margin;
        var height = boxes.Values.Max(b => b.Bottom) + metrics.Margin;
        var shown = boxes.Where(b => !IsWaypoint(b.Key)).ToDictionary(b => b.Key, b => b.Value, StringComparer.Ordinal);
        return new DiagramPlacement(shown, Route(edges, boxes, chains, metrics), width, height);
    }

    private static bool IsWaypoint(string id) => id.Length > 0 && id[0] == WaypointMark;

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
    /// Nodes of the placement before keep their order (by their place then); a new one – or a waypoint – goes where the
    /// middle of its placed neighbours is, otherwise to the end of its column. A waypoint's neighbours are followed along
    /// its edge to the nodes at both ends.
    /// </summary>
    private static void OrderAgainst(Dictionary<int, List<string>> layers, Dictionary<string, List<string>> neighbours, DiagramPlacement previous)
    {
        double Key(string id)
        {
            if (previous.Nodes.TryGetValue(id, out var box))
            {
                return box.CenterY;
            }

            var placed = Ends(id, neighbours).Where(previous.Nodes.ContainsKey).Select(n => previous.Nodes[n].CenterY).ToList();
            return placed.Count == 0 ? double.MaxValue : placed.Average();
        }

        foreach (var layer in layers.Keys.ToList())
        {
            layers[layer] = [.. layers[layer].OrderBy(Key)];
        }
    }

    /// <summary>The neighbours of a node; for a waypoint the real nodes its edge connects.</summary>
    private static IEnumerable<string> Ends(string id, Dictionary<string, List<string>> neighbours)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal) { id };
        var queue = new Queue<string>(neighbours[id]);
        while (queue.TryDequeue(out var next))
        {
            if (!seen.Add(next))
            {
                continue;
            }

            if (!IsWaypoint(next))
            {
                yield return next;
            }
            else if (IsWaypoint(id))
            {
                foreach (var further in neighbours[next])
                {
                    queue.Enqueue(further);
                }
            }
        }
    }

    /// <summary>
    /// Columns from the tallest outwards: a node is placed level with the middle of its neighbours in the columns placed
    /// already, as far as the nodes above it allow; the column is then moved up as a whole by its mean distance from where
    /// its nodes wanted to be. The tallest column is simply stacked. Returns the top of each node.
    /// </summary>
    private static Dictionary<string, double> PlaceVertically(
        Dictionary<int, List<string>> layers, Dictionary<string, List<string>> neighbours, Func<string, double> heightOf, LayoutMetrics metrics)
    {
        var centers = new Dictionary<string, double>(StringComparer.Ordinal);
        var tallest = layers.OrderByDescending(l => l.Value.Sum(heightOf)).ThenBy(l => Math.Abs(l.Key)).First().Key;
        var sequence = layers.Keys.OrderBy(l => Math.Abs(l - tallest)).ThenBy(l => l);
        foreach (var layer in sequence)
        {
            var ids = layers[layer];
            var wanted = ids.Select(id => neighbours[id].Where(centers.ContainsKey).Select(n => centers[n]).DefaultIfEmpty(double.NaN).Average()).ToList();
            var placed = new double[ids.Count];
            var top = metrics.Margin;
            for (var i = 0; i < ids.Count; i++)
            {
                var half = heightOf(ids[i]) / 2;
                placed[i] = double.IsNaN(wanted[i]) ? top + half : Math.Max(top + half, wanted[i]);
                top = placed[i] + half + metrics.NodeGap;
            }

            // Pushed down by the nodes above, the column moves back up as far as its nodes want on average, not above the margin.
            var off = ids.Select((_, i) => wanted[i] - placed[i]).Where(d => !double.IsNaN(d)).DefaultIfEmpty(0).Average();
            var shift = Math.Max(metrics.Margin - (placed[0] - (heightOf(ids[0]) / 2)), Math.Min(0, off));
            for (var i = 0; i < ids.Count; i++)
            {
                centers[ids[i]] = placed[i] + shift;
            }
        }

        // A column may have been moved up less than another wanted: start all at the margin.
        var tops = centers.ToDictionary(c => c.Key, c => c.Value - (heightOf(c.Key) / 2), StringComparer.Ordinal);
        var first = tops.Values.Min();
        foreach (var id in tops.Keys.ToList())
        {
            tops[id] += metrics.Margin - first;
        }

        return tops;
    }

    /// <summary>
    /// A path of cubic curves per edge. Between columns from the right side of the left node to the left side of the
    /// right one, straight through the lanes of the columns between; within a column in an arc on the right; a self
    /// reference as a loop on the right. Several edges between the same two nodes leave and arrive side by side.
    /// </summary>
    private static List<EdgePath> Route(
        IReadOnlyList<LayoutEdge> edges, Dictionary<string, NodeBox> boxes, List<string>?[] chains, LayoutMetrics metrics)
    {
        var paths = new List<EdgePath>();
        var drawn = edges.Select((edge, index) => (Edge: edge, Index: index)).Where(e => boxes.ContainsKey(e.Edge.From) && boxes.ContainsKey(e.Edge.To));
        foreach (var pair in drawn.GroupBy(e => string.CompareOrdinal(e.Edge.From, e.Edge.To) <= 0 ? (e.Edge.From, e.Edge.To) : (e.Edge.To, e.Edge.From)))
        {
            var list = pair.ToList();
            for (var i = 0; i < list.Count; i++)
            {
                var (edge, index) = list[i];
                var port = (i - ((list.Count - 1) / 2.0)) * PortSpacing;
                var (from, to) = (boxes[edge.From], boxes[edge.To]);
                var points = from.Id == to.Id ? Loop(from, port, metrics)
                    : from.Layer == to.Layer ? Arc(from, to, port, metrics)
                    : Across([.. chains[index]!.Select(id => boxes[id])], port, metrics);
                paths.Add(new EdgePath(edge.Id, edge.From, edge.To, points));
            }
        }

        return paths;
    }

    private static PathPoint[] Loop(NodeBox node, double port, LayoutMetrics metrics)
    {
        var (x, y, loop) = (node.Right, node.CenterY + port, metrics.LayerGap / 3);
        return [new(x, y - 8), new(x + loop, y - loop), new(x + loop, y + loop), new(x, y + 8)];
    }

    private static PathPoint[] Arc(NodeBox from, NodeBox to, double port, LayoutMetrics metrics)
    {
        var bulge = (metrics.LayerGap / 3) + (Math.Abs(from.Order - to.Order) * 4);
        var x = from.Right;
        return [new(x, from.CenterY + port), new(x + bulge, from.CenterY + port), new(x + bulge, to.CenterY + port), new(x, to.CenterY + port)];
    }

    /// <summary>From the first box to the last through the lanes between, drawn left to right and turned if the edge runs leftwards.</summary>
    private static PathPoint[] Across(List<NodeBox> chain, double port, LayoutMetrics metrics)
    {
        var leftwards = chain[0].Layer > chain[^1].Layer;
        if (leftwards)
        {
            chain.Reverse();
        }

        var points = new List<PathPoint>();
        for (var i = 0; i < chain.Count - 1; i++)
        {
            var (left, right) = (chain[i], chain[i + 1]);
            var start = new PathPoint(left.Right, left.CenterY + (i == 0 ? port : 0));
            var end = new PathPoint(right.X, right.CenterY + (i == chain.Count - 2 ? port : 0));
            if (i == 0)
            {
                points.Add(start);
            }
            else
            {
                points.AddRange([points[^1], start, start]); // straight through the lane
            }

            var pull = Math.Max(metrics.LayerGap / 2, (end.X - start.X) / 3);
            points.AddRange([new(start.X + pull, start.Y), new(end.X - pull, end.Y), end]);
        }

        if (leftwards)
        {
            points.Reverse();
        }

        return [.. points];
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

/// <summary>
/// A drawn edge from <see cref="From"/> to <see cref="To"/>: cubic Bézier curves one after the other – a start point, then
/// two control points and an end point per curve.
/// </summary>
public sealed record EdgePath(string Id, string From, string To, IReadOnlyList<PathPoint> Points)
{
    /// <summary>The path as SVG path data ("M x y C … C …").</summary>
    public string Svg
    {
        get
        {
            var text = new StringBuilder();
            text.Append(CultureInfo.InvariantCulture, $"M {Points[0].X:0.#} {Points[0].Y:0.#}");
            for (var i = 1; i + 2 < Points.Count; i += 3)
            {
                text.Append(CultureInfo.InvariantCulture,
                    $" C {Points[i].X:0.#} {Points[i].Y:0.#}, {Points[i + 1].X:0.#} {Points[i + 1].Y:0.#}, {Points[i + 2].X:0.#} {Points[i + 2].Y:0.#}");
            }

            return text.ToString();
        }
    }
}

/// <summary>The result of <see cref="DiagramLayout.Arrange"/>: boxes by node id, edge paths and the size of the whole.</summary>
public sealed record DiagramPlacement(IReadOnlyDictionary<string, NodeBox> Nodes, IReadOnlyList<EdgePath> Edges, double Width, double Height);
