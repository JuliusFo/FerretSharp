using FerretSharp.Core.Data;
using FerretSharp.Core.Relations;
using FerretSharp.Core.Schema;

namespace FerretSharp.UI.State;

/// <summary>
/// What a record diagram tab shows (WP-60): the start rows as saved (keys as text), the diagram read from them, its layout,
/// the zoom and the rows marked for a copy. Loading reads the start rows again and does what was done before – a new tab
/// expands the start rows on both sides –, laid out against the placement before (F5 keeps the order). The rows and their
/// values stay in memory; the tab saves only the keys and the steps. Changed on the UI thread only (the view calls it
/// through <c>CallDbAsync</c>).
/// </summary>
public sealed class RecordDiagramState
{
    private readonly DiagramZoom _zoom = new();
    private readonly HashSet<RecordNode> _marked = [];

    /// <summary>What to do when loading: null for a new diagram (the start rows expanded on both sides).</summary>
    private IReadOnlyList<RecordStep>? _steps;

    /// <summary>Steps of a new diagram before the user did anything; null for one restored from saved steps.</summary>
    private int? _initialSteps;

    /// <param name="steps">Saved steps; null for a new diagram.</param>
    public RecordDiagramState(TableRef table, bool rowIds, IReadOnlyList<IReadOnlyList<string>> keys, IReadOnlyList<RecordStep>? steps)
    {
        Table = table;
        RowIds = rowIds;
        Keys = keys;
        _steps = steps;
    }

    /// <summary>The table of the start rows.</summary>
    public TableRef Table { get; }

    /// <summary>The keys are ROWIDs.</summary>
    public bool RowIds { get; }

    /// <summary>The start rows' keys as text (<see cref="RecordKeys"/>).</summary>
    public IReadOnlyList<IReadOnlyList<string>> Keys { get; }

    public RecordDiagram? Diagram { get; private set; }

    public DiagramPlacement? Placement { get; private set; }

    /// <summary>
    /// What to save: the steps of the diagram shown, or those to do if it has not been loaded yet – or found none of its start
    /// rows (review: they may come back after a rollback, and their steps with them). Empty for a new diagram.
    /// </summary>
    public IReadOnlyList<RecordStep> Steps => Diagram is { Start.Count: > 0 } diagram ? diagram.Steps : _steps ?? [];

    /// <summary>Something was done since the start rows were expanded (the first two steps of a new diagram).</summary>
    public bool Changed => Steps.Count > InitialSteps;

    /// <summary>None of the start rows exists (any more): deleted, or their keys no longer fit the table.</summary>
    public bool StartGone => Diagram is { Start.Count: 0 };

    public double Zoom => _zoom.Value;

    public bool CanZoomIn => _zoom.CanZoomIn;

    public bool CanZoomOut => _zoom.CanZoomOut;

    public void ZoomIn() => _zoom.ZoomIn();

    public void ZoomOut() => _zoom.ZoomOut();

    public void ResetZoom() => _zoom.Reset();

    /// <summary>Rows marked for a copy, in no particular order.</summary>
    public IReadOnlyCollection<RecordNode> Marked => _marked;

    /// <summary>The table of the marked rows if they are all of one; a copy starts from rows of one table.</summary>
    public TableRef? MarkedTable => _marked.Select(n => n.Table).Distinct().ToList() is [var table] ? table : null;

    public bool IsMarked(RecordNode node) => _marked.Contains(node);

    public void ToggleMark(RecordNode node)
    {
        if (!_marked.Remove(node) && Diagram?.IsShown(node) == true)
        {
            _marked.Add(node);
        }
    }

    public void ClearMarks() => _marked.Clear();

    /// <summary>
    /// Reads the start rows again and does the steps (the first time, a new diagram expands them on both sides). The
    /// diagram shown before stays until the new one is complete: a failed or cancelled load leaves it.
    /// </summary>
    /// <param name="table">Columns and keys of the start table as they are now.</param>
    public async Task LoadAsync(
        RowTraversal traversal, RelationGraph relations, TableDetails table, IProgress<TraversalProgress>? progress, CancellationToken cancellationToken)
    {
        // The steps of the diagram shown (a snapshot, taken now) or those saved; null for a new diagram. Kept if no start row
        // is found (review: the rows may be back after a rollback, and their steps with them).
        var steps = Diagram is { Start.Count: > 0 } shown ? shown.Steps : _steps;
        var keys = Keys.Select(k => RecordKeys.FromText(table, RowIds, k)).OfType<RowKey>().ToList();
        var diagram = await RecordDiagram.StartAsync(traversal, relations, table, keys, cancellationToken);
        if (diagram.Start.Count > 0)
        {
            if (steps is not null)
            {
                await diagram.ReplayAsync(steps, progress, cancellationToken);
            }
            else
            {
                await diagram.ExpandAsync(diagram.Start, DiagramSide.Parents, progress, cancellationToken);
                await diagram.ExpandAsync(diagram.Start, DiagramSide.Children, progress, cancellationToken);
                _initialSteps = diagram.Steps.Count;
            }
        }

        var previous = Diagram is null ? null : Placement;
        Diagram = diagram;
        _steps = diagram.Start.Count > 0 ? diagram.Steps : steps;
        _marked.RemoveWhere(n => !diagram.IsShown(n));
        Arrange(previous);
    }

    /// <summary>Back to the start rows expanded on both sides, laid out afresh; the next <see cref="LoadAsync"/> builds it.</summary>
    public void Reset()
    {
        _steps = null;
        _initialSteps = null;
        Placement = null;
        Diagram = null;
        _marked.Clear();
    }

    /// <summary>All pending relationships of a row on one side. A cancelled or failed read keeps what came in before.</summary>
    public async Task ExpandAsync(RecordNode node, DiagramSide side, IProgress<TraversalProgress>? progress, CancellationToken cancellationToken)
    {
        if (Diagram is not { } diagram)
        {
            return;
        }

        try
        {
            await diagram.ExpandAsync([node], side, progress, cancellationToken);
        }
        finally
        {
            Arrange(Placement);
        }
    }

    /// <summary>One relationship of a row (the context menu).</summary>
    public async Task ExpandAsync(
        RecordNode node, ForeignKeyInfo relation, DiagramSide side, IProgress<TraversalProgress>? progress, CancellationToken cancellationToken)
    {
        if (Diagram is not { } diagram)
        {
            return;
        }

        try
        {
            await diagram.ExpandAsync(node, relation, side, progress, cancellationToken);
        }
        finally
        {
            Arrange(Placement);
        }
    }

    public void Open(RecordGroup group)
    {
        if (Diagram?.Open(group).Count > 0)
        {
            Arrange(Placement);
        }
    }

    /// <summary>
    /// Steps of a new diagram before the user did anything: the start rows expanded to the left, then to the right. Restored
    /// from saved steps, the leading expansions of all start rows count as these.
    /// </summary>
    private int InitialSteps => _initialSteps ?? (Diagram is null ? 0 : Diagram.Steps.TakeWhile((s, i) => i < 2 && s.Kind == RecordStepKind.Expand
        && s.Relation is null && s.Nodes.SequenceEqual(Diagram.Start.Select(n => n.Id))).Count());

    private void Arrange(DiagramPlacement? previous)
    {
        var (nodes, edges) = Diagram!.ToLayout();
        Placement = DiagramLayout.Arrange(nodes, edges, LayoutMetrics.Default, previous);
    }
}
