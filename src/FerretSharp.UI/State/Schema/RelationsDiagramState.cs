using FerretSharp.Core.Relations;
using FerretSharp.Core.Schema;

namespace FerretSharp.UI.State;

/// <summary>
/// What the "Relations" view of a table tab shows (WP-46): the diagram around a table – the tab's own or one put in the
/// middle since –, its depth, the kinds of relationship shown, what was expanded and the zoom. Expanding and opening
/// groups are remembered as steps and replayed when the diagram is built again (F5, schema reloaded, a kind switched).
/// Only in memory, like the rest of the view.
/// </summary>
public sealed class RelationsDiagramState(TableRef home)
{
    private static readonly double[] ZoomSteps = [0.5, 0.67, 0.8, 0.9, 1, 1.1, 1.25, 1.5];

    private readonly List<Step> _steps = [];
    private readonly HashSet<FkSource> _hiddenSources = [];
    private RelationGraph? _graph;

    /// <summary>The tab's table: where "Reset" goes back to.</summary>
    public TableRef Home { get; } = home;

    /// <summary>The table in the middle.</summary>
    public TableRef Focus { get; private set; } = home;

    public int Depth { get; private set; } = 1;

    public double Zoom { get; private set; } = 1;

    public RelationDiagram? Diagram { get; private set; }

    public DiagramPlacement? Placement { get; private set; }

    /// <summary>Kinds of relationship the schema has, in enum order: a switch for each only when there are several.</summary>
    public IReadOnlyList<FkSource> Sources { get; private set; } = [];

    /// <summary>Something differs from the diagram the view opened with.</summary>
    public bool Changed => Focus != Home || Depth != 1 || _steps.Count > 0 || _hiddenSources.Count > 0;

    public bool Shows(FkSource source) => !_hiddenSources.Contains(source);

    /// <summary>The schema's relationships, now or after it was loaded again: builds the diagram and replays the steps.</summary>
    public void Load(RelationGraph graph)
    {
        _graph = graph;
        Sources = [.. graph.Relations.Select(r => r.Source).Distinct().Order()];
        Rebuild();
    }

    public void Expand(TableRef table, DiagramSide side)
    {
        if (Diagram is null || Diagram.HiddenCount(table, side) == 0)
        {
            return;
        }

        Diagram.Expand(table, side);
        _steps.Add(new Step(table, side, Group: false));
        Arrange(Placement);
    }

    public void Open(DiagramGroup group)
    {
        if (Diagram is null || Diagram.Open(group).Count == 0)
        {
            return;
        }

        _steps.Add(new Step(group.Table, group.Side, Group: true));
        Arrange(Placement);
    }

    /// <summary>Puts <paramref name="table"/> in the middle: a new diagram around it, with depth and kinds as they are.</summary>
    public void Recenter(TableRef table)
    {
        Focus = table;
        _steps.Clear();
        Rebuild();
    }

    public void SetDepth(int depth)
    {
        Depth = Math.Clamp(depth, 1, 2);
        Rebuild();
    }

    public void ToggleSource(FkSource source)
    {
        if (!_hiddenSources.Remove(source))
        {
            _hiddenSources.Add(source);
        }

        Rebuild();
    }

    /// <summary>Back to the diagram the view opened with: the tab's table, one level, all kinds; the zoom stays.</summary>
    public void Reset()
    {
        Focus = Home;
        Depth = 1;
        _steps.Clear();
        _hiddenSources.Clear();
        Rebuild();
    }

    public bool CanZoomIn => Zoom < ZoomSteps[^1];

    public bool CanZoomOut => Zoom > ZoomSteps[0];

    public void ZoomIn() => Zoom = ZoomSteps.FirstOrDefault(z => z > Zoom, ZoomSteps[^1]);

    public void ZoomOut() => Zoom = ZoomSteps.LastOrDefault(z => z < Zoom, ZoomSteps[0]);

    public void ResetZoom() => Zoom = 1;

    private void Rebuild()
    {
        if (_graph is null)
        {
            return;
        }

        var sources = DiagramOptions.AllSources.Where(Shows).ToHashSet();
        Diagram = new RelationDiagram(_graph, Focus, DiagramOptions.Default with { Depth = Depth, Sources = sources });
        foreach (var step in _steps)
        {
            if (!step.Group)
            {
                Diagram.Expand(step.Table, step.Side);
            }
            else if (Diagram.Groups.FirstOrDefault(g => g.Table == step.Table && g.Side == step.Side) is { } group)
            {
                Diagram.Open(group);
            }
        }

        Arrange(previous: null);
    }

    private void Arrange(DiagramPlacement? previous)
    {
        var (nodes, edges) = Diagram!.ToLayout();
        Placement = DiagramLayout.Arrange(nodes, edges, LayoutMetrics.Default, previous);
    }

    /// <summary>Expanding one side of a table, or opening the group it became.</summary>
    private sealed record Step(TableRef Table, DiagramSide Side, bool Group);
}
