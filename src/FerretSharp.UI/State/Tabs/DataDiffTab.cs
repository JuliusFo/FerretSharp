using FerretSharp.Core.Connections;
using FerretSharp.Core.Data;
using FerretSharp.Core.Schema;
using FerretSharp.Core.Workspaces;

namespace FerretSharp.UI.State;

/// <summary>What the data diff (WP-44) found for one table: its rows, an error to show, or that it is being read.</summary>
/// <param name="Diff">The differing rows; null while reading or after an error.</param>
/// <param name="Error">Why the table could not be compared (explained where the user can act, <see cref="DataDiffErrors"/>).</param>
/// <param name="Failure">The database error behind <paramref name="Error"/>, for "Details"; null for a refusal.</param>
public sealed record DataDiffResult(DataDiffEntry Entry, TableDiff? Diff, string? Error, DatabaseException? Failure = null, bool Reading = false)
{
    public static DataDiffResult Pending(DataDiffEntry entry) => new(entry, null, null, Reading: true);
}

/// <summary>
/// The tab of a workspace's data diff (WP-44): the tables of its start point (<see cref="Workspace.DataDiff"/>) with what the
/// last comparison found – only in memory, also values of Prod. One per workspace.
/// </summary>
public sealed class DataDiffTab(Guid workspaceId) : WorkspaceTab(workspaceId, "diff-")
{
    private readonly Dictionary<TableRef, DataDiffResult> _results = [];
    private CancellationTokenSource? _running;

    /// <summary>The start point the results belong to; a new start point drops them.</summary>
    public long? ResultsScn { get; private set; }

    /// <summary>When the last comparison ended; null before the first.</summary>
    public DateTimeOffset? ComparedAt { get; private set; }

    /// <summary>The table whose rows are shown.</summary>
    public TableRef? Selected { get; set; }

    /// <summary>A comparison is running (<see cref="Cancel"/> ends it).</summary>
    public bool Running => _running is not null;

    public DataDiffResult? ResultOf(TableRef table) => _results.GetValueOrDefault(table);

    public override TabState ToState(IReadOnlyList<WorkspaceTab> workspaceTabs) => TabState.OfDataDiff(new DataDiffTabState());

    /// <summary>A comparison of <paramref name="scn"/> starts; results of another start point go. Null if one is running already.</summary>
    internal CancellationToken? Begin(long scn)
    {
        if (_running is not null)
        {
            return null;
        }

        if (ResultsScn != scn)
        {
            _results.Clear();
            ResultsScn = scn;
        }

        _running = new CancellationTokenSource();
        return _running.Token;
    }

    internal void Set(DataDiffResult result) => _results[result.Entry.Table] = result;

    internal void End(DateTimeOffset at)
    {
        _running?.Dispose();
        _running = null;
        ComparedAt = at;
    }

    public void Cancel() => _running?.Cancel();

    /// <summary>The start point was discarded or set anew: nothing found so far belongs to it.</summary>
    internal void Clear()
    {
        _results.Clear();
        ResultsScn = null;
        ComparedAt = null;
    }

    internal void Forget(TableRef table) => _results.Remove(table);
}
