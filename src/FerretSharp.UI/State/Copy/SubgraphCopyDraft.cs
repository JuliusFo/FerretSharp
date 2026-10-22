using FerretSharp.Core.Connections;
using FerretSharp.Core.Data;
using FerretSharp.Core.Relations;
using FerretSharp.Core.Schema;

namespace FerretSharp.UI.State;

/// <summary>A workspace of another open connection a copy can go to (WP-42).</summary>
/// <param name="Label">"TEST › Workspace 1": connection (with group) and workspace.</param>
/// <param name="Writable">The workspace may write now (a locked Prod must be unlocked first).</param>
/// <param name="Data">The workspace's read path (existing rows are looked up there).</param>
public sealed record CopyTarget(
    Guid ConnectionId, Guid WorkspaceId, string Label, ConnectionKind Kind, bool Writable, SchemaCache Schema,
    Func<CancellationToken, Task<IDataAccess>> Data);

/// <summary>Where a copy stands in the dialog.</summary>
public enum CopyStage
{
    /// <summary>Choosing target and children; nothing read yet, or the choice changed since.</summary>
    Choosing,
    Loading,

    /// <summary>The plan is there for the current choice.</summary>
    Planned,
}

/// <summary>
/// The copy of selected rows into a workspace of another connection, as the dialog builds it (WP-42): the target, the
/// paths of children (one level along every relationship by default, up to three), parents along relationships the user
/// did not switch off, then the rows collected on the source workspace's read path and checked against the target
/// (<see cref="SubgraphCopyPlan"/>) with a conflict mode per table. Not thread-safe: the dialog's thread only.
/// </summary>
public sealed class SubgraphCopyDraft
{
    private readonly HashSet<string> _paths = new(StringComparer.Ordinal);
    private readonly HashSet<string> _skippedParents = new(StringComparer.Ordinal);
    private readonly Dictionary<TableRef, ConflictMode> _modes = [];
    private readonly IReadOnlyList<RowKey> _keys;
    private readonly string _sourceOwner;
    private readonly Func<CancellationToken, Task<IDataAccess>> _sourceData;
    private readonly Func<TableRef, CancellationToken, Task<TableDetails?>> _sourceDetails;

    /// <param name="sourceOwner">The schema the source connection browses.</param>
    /// <param name="sourceData">The source workspace's read path.</param>
    /// <param name="sourceDetails">Columns and keys of a source table; null if the connection does not know it.</param>
    public SubgraphCopyDraft(
        string sourceName, string sourceOwner, RelationGraph relations, TableDetails start, IReadOnlyList<RowKey> keys,
        Func<CancellationToken, Task<IDataAccess>> sourceData, Func<TableRef, CancellationToken, Task<TableDetails?>> sourceDetails,
        IReadOnlyList<CopyTarget> targets)
    {
        SourceName = sourceName;
        _sourceOwner = sourceOwner;
        Relations = relations;
        Start = start;
        _keys = keys;
        _sourceData = sourceData;
        _sourceDetails = sourceDetails;
        Targets = targets;
        Target = targets.FirstOrDefault(t => t.Writable) ?? targets.FirstOrDefault();
        foreach (var relation in relations.ChildrenOf(start.Table.Ref))
        {
            _paths.Add(KeyOf([relation]));
        }
    }

    public string SourceName { get; }

    public RelationGraph Relations { get; }

    public TableDetails Start { get; }

    /// <summary>Selected rows the copy starts from.</summary>
    public int StartRows => _keys.Count;

    public IReadOnlyList<CopyTarget> Targets { get; }

    public CopyTarget? Target { get; private set; }

    public CopyStage Stage { get; private set; } = CopyStage.Choosing;

    /// <summary>Rows found and the plan for the current choice; null before loading or after the choice changed.</summary>
    public RowGraph? Graph { get; private set; }

    public SubgraphCopyPlan? Plan { get; private set; }

    /// <summary>Why the last load failed (too many rows, a database error); null otherwise.</summary>
    public string? LoadError { get; private set; }

    public IReadOnlyDictionary<TableRef, ConflictMode> Modes => _modes;

    public void ChooseTarget(CopyTarget target)
    {
        if (Target == target)
        {
            return;
        }

        Target = target;
        _modes.Clear();
        Changed();
    }

    /// <summary>The child relationships one level below <paramref name="path"/> (the start table's for an empty path).</summary>
    public IReadOnlyList<ForeignKeyInfo> ChildrenBelow(IReadOnlyList<ForeignKeyInfo> path) =>
        path.Count >= TraversalPlan.MaxDepth ? [] : [.. Relations.ChildrenOf(path.Count == 0 ? Start.Table.Ref : path[^1].From)];

    public bool IsChosen(IReadOnlyList<ForeignKeyInfo> path) => _paths.Contains(KeyOf(path));

    /// <summary>
    /// Switches a path of children on or off: on also switches on the paths it extends (the rows below come through them),
    /// off also the paths that extend it.
    /// </summary>
    public void Toggle(IReadOnlyList<ForeignKeyInfo> path)
    {
        var key = KeyOf(path);
        if (_paths.Contains(key))
        {
            _paths.RemoveWhere(p => p == key || p.StartsWith(key + "\u0002", StringComparison.Ordinal));
        }
        else
        {
            for (var length = 1; length <= path.Count; length++)
            {
                _paths.Add(KeyOf(path.Take(length).ToList()));
            }
        }

        Changed();
    }

    public bool IsParentSkipped(ForeignKeyInfo relation) => _skippedParents.Contains(RelationGraph.KeyOf(relation));

    /// <summary>Parents along this relationship are collected or not (a model relationship to a large table, say).</summary>
    public void ToggleParent(ForeignKeyInfo relation)
    {
        var key = RelationGraph.KeyOf(relation);
        if (!_skippedParents.Remove(key))
        {
            _skippedParents.Add(key);
        }

        Changed();
    }

    /// <summary>
    /// The tables whose parents are collected, as roots of the parent tree: the start table, then the tables the chosen paths
    /// of children reach, each once.
    /// </summary>
    public IReadOnlyList<TableRef> ParentRoots =>
        [.. new[] { Start.Table.Ref }.Concat(Traversal.Children.SelectMany(p => p).Select(r => r.From)).Distinct()];

    /// <summary>
    /// The relationships in which <paramref name="table"/> references others: one level of the parent tree. Those of the chosen
    /// paths of children are left out – they lead back to the rows the children were found from (E2E: AUFTRAG → KUNDEN under
    /// the orders of a customer).
    /// </summary>
    public IReadOnlyList<ForeignKeyInfo> ParentsBelow(TableRef table)
    {
        var onPaths = Traversal.Children.SelectMany(p => p).ToHashSet();
        return [.. Relations.ParentsOf(table).Where(r => !onPaths.Contains(r))];
    }

    /// <summary>Rows reached along a path of children by the last read; null before reading or after the choice changed.</summary>
    public int? ReachedAlong(IReadOnlyList<ForeignKeyInfo> path) => Stage == CopyStage.Planned ? Graph?.ReachedAlong(path) : null;

    /// <summary>Parent rows linked along a relationship by the last read; null before reading or after the choice changed.</summary>
    public int? ParentsAlong(ForeignKeyInfo relation) =>
        Stage == CopyStage.Planned && Graph is { } graph && !IsParentSkipped(relation) ? graph.ParentsAlong(relation) : null;

    public ConflictMode ModeOf(TableCopy table) => SubgraphCopyPlan.ModeOf(table, _modes);

    public void SetMode(TableRef table, ConflictMode mode) => _modes[table] = mode;

    public IReadOnlyList<CopyIssue> Issues => Plan?.IssuesWith(_modes) ?? [];

    public bool CanCopy => Stage == CopyStage.Planned && Plan is { } plan && Target is { Writable: true } && plan.CanCopy(_modes);

    /// <summary>The traversal plan of the current choice.</summary>
    public TraversalPlan Traversal =>
        new([.. _paths.Select(PathOf).OfType<IReadOnlyList<ForeignKeyInfo>>().OrderBy(p => p.Count)], new HashSet<string>(_skippedParents, StringComparer.Ordinal));

    /// <summary>
    /// Collects the rows on the source workspace's read path and checks them against the target. A refusal (too many rows)
    /// ends up in <see cref="LoadError"/>; cancelling and database errors return to choosing and go to the caller.
    /// </summary>
    public async Task LoadAsync(IProgress<TraversalProgress>? progress, CancellationToken cancellationToken)
    {
        if (Target is not { } target)
        {
            return;
        }

        Stage = CopyStage.Loading;
        LoadError = null;
        Graph = null;
        Plan = null;
        try
        {
            var traversal = Traversal;
            var collector = new RowTraversal(await _sourceData(cancellationToken), Relations, _sourceDetails, wholeLobs: true);
            var graph = await collector.CollectAsync(Start, _keys, traversal, progress, cancellationToken);
            var planner = new SubgraphCopyPlanner(_sourceOwner, target.Schema, await target.Data(cancellationToken));
            Plan = await planner.PlanAsync(graph, [.. _skippedParents.Select(Relations.Find).OfType<ForeignKeyInfo>()], cancellationToken);
            Graph = graph;
            Stage = CopyStage.Planned;
        }
        catch (RefusedException ex)
        {
            LoadError = ex.Message;
            Stage = CopyStage.Choosing;
        }
        catch
        {
            // Cancelled, or a database error the caller reports (a lost session goes to its connection).
            Stage = CopyStage.Choosing;
            throw;
        }
    }

    /// <summary>The writes for the target; only when <see cref="CanCopy"/>.</summary>
    public CopyWrite Write(int lockWaitSeconds) =>
        CanCopy ? Plan!.ToWrite(_modes, SourceName, lockWaitSeconds) : throw new InvalidOperationException("The copy cannot run.");

    /// <summary>The choice changed: what was loaded no longer fits.</summary>
    private void Changed()
    {
        if (Stage == CopyStage.Planned)
        {
            Stage = CopyStage.Choosing;
        }
    }

    private IReadOnlyList<ForeignKeyInfo>? PathOf(string key)
    {
        var relations = key.Split('\u0002').Select(Relations.Find).ToList();
        return relations.All(r => r is not null) ? [.. relations.OfType<ForeignKeyInfo>()] : null;
    }

    private static string KeyOf(IReadOnlyList<ForeignKeyInfo> path) => RelationGraph.PathKey(path);
}
