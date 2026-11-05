using FerretSharp.Core.Relations;
using FerretSharp.Core.Schema;

namespace FerretSharp.UI.State;

/// <summary>
/// The ways from a table tab's table to another one in its "Relations" view (WP-74): the target, the tables ways avoid,
/// the ways found so far in order (<see cref="RelationPath.Compare"/>), the one chosen, and the diagram of all of them.
/// The search runs on a background thread a block at a time (<see cref="MoreAsync"/>); each way found waits in
/// <see cref="Collect"/> for the UI thread, so the list fills while it runs. A new target, graph or set of avoided tables
/// starts a new search and lets a running block end unseen. Everything but the background block runs on the UI thread.
/// </summary>
public sealed class RelationPathsState(TableRef start)
{
    /// <summary>Ways per block: the search pauses after them, "More ways" goes on (decision of the user, 2026-11-06).</summary>
    public const int BlockSize = 50;

    private readonly List<RelationPath> _paths = [];
    private readonly object _gate = new();
    private List<RelationPath> _pending = [];
    private RelationGraph? _graph;
    private RelationPathSearch? _search;
    private CancellationTokenSource? _running;
    private IReadOnlySet<TableRef> _avoided = new HashSet<TableRef>();

    public TableRef Start { get; } = start;

    public TableRef? Target { get; private set; }

    /// <summary>Tables no way goes through (the connection's, from the settings).</summary>
    public IReadOnlySet<TableRef> Avoided => _avoided;

    /// <summary>The ways found, shortest first.</summary>
    public IReadOnlyList<RelationPath> Paths => _paths;

    /// <summary>The way chosen in the list; null for none.</summary>
    public RelationPath? Selected { get; private set; }

    public PathDiagram? Diagram { get; private set; }

    public DiagramPlacement? Placement { get; private set; }

    /// <summary>A block is being searched.</summary>
    public bool Running => _running is not null;

    /// <summary>Every way is found.</summary>
    public bool Done => _search?.Done ?? false;

    /// <summary>The length the search is at (progress while it runs).</summary>
    public int Length => _search?.Length ?? 0;

    /// <summary>The last block was cancelled; "More ways" goes on where it stopped.</summary>
    public bool Cancelled { get; private set; }

    /// <summary>
    /// The relationships of the kinds shown: a new search if they are other ones than before – by content, since every
    /// build of the linked project brings new records for the same relationships (review).
    /// </summary>
    public void Load(RelationGraph graph)
    {
        if (_graph is not null && RelationGraph.SameRelations(_graph.Relations, graph.Relations))
        {
            return;
        }

        _graph = graph;
        Restart();
    }

    public void SetTarget(TableRef? target)
    {
        target = target == Start ? null : target;
        if (target == Target)
        {
            return;
        }

        Target = target;
        Restart();
    }

    public void SetAvoided(IReadOnlySet<TableRef> avoided)
    {
        if (avoided.SetEquals(_avoided))
        {
            return;
        }

        _avoided = avoided.ToHashSet();
        Restart();
    }

    /// <summary>Tables shown in the menu "Path to": at most this many.</summary>
    public const int CandidateLimit = 30;

    /// <summary>
    /// The tables to choose a target from for <paramref name="query"/>, as the quick open finds them: by table name (or
    /// synonym) and entity name, the best match first, then by name; the start left out.
    /// </summary>
    public static IReadOnlyList<TableSummary> Candidates(
        IEnumerable<TableSummary> tables, Func<TableRef, string?> entityOf, string? query, TableRef start, int limit = CandidateLimit)
    {
        var terms = Core.Query.NameRank.Terms(query);
        return
        [
            .. tables
                .Where(t => t.Ref != start)
                .Select(t => (Table: t, Match: Core.Query.NameRank.Best(
                    Core.Query.NameRank.Of(t.DisplayName, terms),
                    entityOf(t.Ref) is { } entity ? Core.Query.NameRank.OfClrName(entity, terms) : null)))
                .Where(c => c.Match is not null)
                .OrderBy(c => c.Match)
                .ThenBy(c => c.Table.DisplayName, StringComparer.Ordinal)
                .ThenBy(c => c.Table.Owner, StringComparer.Ordinal)
                .Take(limit)
                .Select(c => c.Table),
        ];
    }

    public void Select(RelationPath? path) => Selected = path is null ? null : _paths.FirstOrDefault(p => p.Key == path.Key);

    /// <summary>Searches the next block on a background thread; nothing if one runs, everything is found, or there is no target.</summary>
    /// <returns>Ends with the block, also when it was cancelled or the search was replaced meanwhile.</returns>
    public Task MoreAsync()
    {
        if (_search is not { Done: false } search || _running is not null)
        {
            return Task.CompletedTask;
        }

        var running = _running = new CancellationTokenSource();
        Cancelled = false;
        var pending = _pending;
        return RunAsync(search, pending, running);
    }

    /// <summary>Stops the running block; the ways it found stay.</summary>
    public void Cancel() => _running?.Cancel();

    /// <summary>Takes the ways the background found into the list and the diagram.</summary>
    /// <returns>Whether the list changed.</returns>
    public bool Collect()
    {
        List<RelationPath> found;
        lock (_gate)
        {
            if (_pending.Count == 0)
            {
                return false;
            }

            // Emptied, not replaced: the running block goes on adding to this list (review: replaced, its later ways were lost).
            found = [.. _pending];
            _pending.Clear();
        }

        // Length for length, so a new way only sorts in among those of its own length.
        foreach (var path in found)
        {
            var at = _paths.FindLastIndex(p => RelationPath.Compare(p, path) <= 0) + 1;
            _paths.Insert(at, path);
        }

        Arrange();
        return true;
    }

    private async Task RunAsync(RelationPathSearch search, List<RelationPath> pending, CancellationTokenSource running)
    {
        try
        {
            await Task.Run(
                () =>
                {
                    for (var i = 0; i < BlockSize && !search.Done; i++)
                    {
                        foreach (var path in search.Next(1, running.Token))
                        {
                            lock (_gate)
                            {
                                pending.Add(path);
                            }
                        }
                    }
                },
                CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            if (ReferenceEquals(search, _search))
            {
                Cancelled = true;
            }
        }
        finally
        {
            if (ReferenceEquals(_running, running))
            {
                _running = null;
            }

            running.Dispose();
        }
    }

    /// <summary>A new search for what is set now: no ways, no choice; the block of the old one goes on unseen until cancelled.</summary>
    private void Restart()
    {
        _running?.Cancel();
        _running = null;
        lock (_gate)
        {
            _pending = [];
        }

        _paths.Clear();
        Selected = null;
        Cancelled = false;
        _search = _graph is not null && Target is { } target ? new RelationPathSearch(_graph, Start, target, _avoided) : null;
        Placement = null;
        Diagram = null;
        Arrange();
    }

    private void Arrange()
    {
        if (Target is not { } target)
        {
            (Diagram, Placement) = (null, null);
            return;
        }

        Diagram = new PathDiagram(Start, target, _paths);
        var (nodes, edges) = Diagram.ToLayout();
        Placement = DiagramLayout.Arrange(nodes, edges, LayoutMetrics.Default, Placement);
    }
}
