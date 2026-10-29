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
    private readonly HashSet<RecordNode> _marked = [];

    /// <summary>What to do when loading: null for a new diagram (the start rows expanded on both sides).</summary>
    private IReadOnlyList<RecordStep>? _steps;

    /// <summary>Steps of a new diagram before the user did anything; null for one restored from saved steps.</summary>
    private int? _initialSteps;

    /// <param name="steps">Saved steps; null for a new diagram.</param>
    /// <param name="depth">Levels an expansion goes (WP-62).</param>
    public RecordDiagramState(TableRef table, bool rowIds, IReadOnlyList<IReadOnlyList<string>> keys, IReadOnlyList<RecordStep>? steps, int depth = 1)
    {
        Table = table;
        RowIds = rowIds;
        Keys = keys;
        _steps = steps;
        Depth = RecordDiagram.ClampDepth(depth);
    }

    /// <summary>The table of the start rows.</summary>
    public TableRef Table { get; }

    /// <summary>The keys are ROWIDs.</summary>
    public bool RowIds { get; }

    /// <summary>The start rows' keys as text (<see cref="RecordKeys"/>).</summary>
    public IReadOnlyList<IReadOnlyList<string>> Keys { get; }

    public RecordDiagram? Diagram { get; private set; }

    /// <summary>
    /// Levels the next expansion of a side goes ("Ebenen" in the header, WP-62): opening, "Zurücksetzen" and the "+n"
    /// handles. One relationship from the context menu goes one level.
    /// </summary>
    public int Depth { get; private set; }

    public DiagramPlacement? Placement { get; private set; }

    /// <summary>
    /// The rows to draw, with their boxes. During an expansion the diagram already holds the rows of the relationships read so
    /// far, but the layout comes only when it ends: those rows wait for it (a progress report rendered them without a box, and
    /// the tab crashed).
    /// </summary>
    public IReadOnlyList<(RecordNode Node, NodeBox Box)> Drawn => Diagram is { } diagram && Placement is { } placement
        ? [.. diagram.Rows.Where(r => placement.Nodes.ContainsKey(r.Node.Id)).Select(r => (r.Node, placement.Nodes[r.Node.Id]))]
        : [];

    /// <summary>
    /// What to save: the steps of the diagram shown, or those to do if it has not been loaded yet – or found none of its start
    /// rows (review: they may come back after a rollback, and their steps with them). Empty for a new diagram.
    /// </summary>
    public IReadOnlyList<RecordStep> Steps => Diagram is { Start.Count: > 0 } diagram ? diagram.Steps : _steps ?? [];

    /// <summary>Something was done since the start rows were expanded (the first two steps of a new diagram).</summary>
    public bool Changed => Steps.Count > InitialSteps;

    /// <summary>None of the start rows exists (any more): deleted, or their keys no longer fit the table.</summary>
    public bool StartGone => Diagram is { Start.Count: 0 };

    /// <summary>The zoom; only in memory (review 3.54.0, B1: one zoom bar for both diagrams).</summary>
    public DiagramZoom Zoom { get; } = new();

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
        // Only opened, at other levels than set now: opened again at these (WP-62). Not through Reset (review): the diagram
        // shown, its placement and the marks stay until the new one is complete.
        var reopen = steps is not null && OnlyOpened(steps) && steps.Any(s => s.Depth != Depth);
        var keys = Keys.Select(k => RecordKeys.FromText(table, RowIds, k)).OfType<RowKey>().ToList();
        var diagram = await RecordDiagram.StartAsync(traversal, relations, table, keys, cancellationToken);
        if (diagram.Start.Count > 0)
        {
            if (steps is not null && !reopen)
            {
                await diagram.ReplayAsync(steps, progress, cancellationToken);
            }
            else
            {
                await diagram.OpenAsync(Depth, progress, cancellationToken);
                _initialSteps = diagram.Steps.Count;
            }
        }

        var previous = Diagram is null ? null : Placement;
        Diagram = diagram;
        _steps = diagram.Start.Count > 0 ? diagram.Steps : steps;
        _marked.RemoveWhere(n => !diagram.IsShown(n));
        Arrange(previous);
    }

    /// <summary>
    /// Sets the levels. A diagram nothing was done in since it was opened is opened again with them by the next
    /// <see cref="LoadAsync"/> (WP-62) – also one not loaded yet, or restored with other levels; true if it is shown and
    /// should be loaded now. Otherwise they apply to the next expansions and a reset.
    /// </summary>
    public bool SetDepth(int depth)
    {
        depth = RecordDiagram.ClampDepth(depth);
        if (depth == Depth)
        {
            return false;
        }

        Depth = depth;
        return Diagram is not null && !StartGone && !Changed;
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
            await diagram.ExpandAsync([node], side, Depth, progress, cancellationToken);
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

    /// <summary>The start rows expanded to the left and to the right, nothing else: what <see cref="RecordDiagram.OpenAsync"/> does.</summary>
    private static bool OnlyOpened(IReadOnlyList<RecordStep> steps) =>
        steps is [{ Kind: RecordStepKind.Expand, Relation: null } left, { Kind: RecordStepKind.Expand, Relation: null } right]
        && left.Side != right.Side && left.Nodes.SequenceEqual(right.Nodes);

    private void Arrange(DiagramPlacement? previous)
    {
        var (nodes, edges) = Diagram!.ToLayout();
        Placement = DiagramLayout.Arrange(nodes, edges, LayoutMetrics.Default, previous);
    }
}
