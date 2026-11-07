using FerretSharp.Core.Connections;
using FerretSharp.Core.Data;
using FerretSharp.Core.Query;
using FerretSharp.Core.Resources;
using FerretSharp.Core.Schema;
using FerretSharp.UI.Resources;

namespace FerretSharp.UI.State;

/// <summary>Rows that may still point to a changed primary key: one incoming relationship and how many rows it counted.</summary>
/// <param name="Table">The referencing table as the user knows it (<see cref="FkTargets.Label"/>).</param>
/// <param name="Count">Done, timed out, or failed – also when the jump could not be built (<see cref="FkJump.Unavailable"/>).</param>
public sealed record KeyReference(FkJump Jump, string Table, FkCount Count)
{
    public ForeignKeyInfo ForeignKey => Jump.ForeignKey;
}

/// <summary>
/// Pending changes of primary keys still have rows pointing to the old keys, or they could not all be counted (ADR 0007
/// addendum, the user's decision: check and warn). Nothing of the tab is written until the user decides.
/// </summary>
/// <param name="Changes">The rows whose key changes (<see cref="RowChange.Id"/>): "write anyway" confirms these.</param>
/// <param name="References">Relationships with rows behind them, or whose count is unknown; never one counted 0.</param>
public sealed record KeyChangeQuestion(
    Guid WorkspaceId, TableTab Tab, IReadOnlyList<Guid> Changes, IReadOnlyList<KeyReference> References, bool DuringCommit)
{
    /// <summary>The grid write a redo applied again: "write anyway" continues that redo.</summary>
    public Guid? RedoOf { get; init; }

    /// <summary>The values "write anyway" confirms per row (<see cref="RowChange.Id"/>): another value later is asked about again.</summary>
    internal IReadOnlyDictionary<Guid, IReadOnlyDictionary<int, object?>> Confirms { get; init; } = new Dictionary<Guid, IReadOnlyDictionary<int, object?>>();
}

/// <summary>Changed primary keys: count the rows still pointing to the old keys before writing (ADR 0007 addendum).</summary>
public sealed partial class WorkspaceEditing
{
    /// <summary>Per workspace: the rows whose changed key the user chose to write anyway, with the values confirmed.</summary>
    private readonly Dictionary<Guid, Dictionary<Guid, IReadOnlyDictionary<int, object?>>> _keysConfirmed = [];

    /// <summary>Asked before a write of changed primary keys; null while nothing waits for an answer.</summary>
    public KeyChangeQuestion? KeyQuestion { get; private set; }

    /// <summary>
    /// The user answered <see cref="KeyQuestion"/>: "write anyway" goes on with the write (and the commit or redo it was part of),
    /// otherwise nothing more is written – the changes stay pending.
    /// </summary>
    public async Task AnswerKeyQuestionAsync(bool write)
    {
        if (KeyQuestion is not { } question)
        {
            return;
        }

        KeyQuestion = null;
        shell.NotifyChanged();
        var workspace = shell.FindWorkspace(question.WorkspaceId);
        if (workspace is null || !write)
        {
            ClearDecisions(question.WorkspaceId);
            return;
        }

        foreach (var (change, values) in question.Confirms)
        {
            KeysConfirmed(question.WorkspaceId)[change] = values;
        }
        if (question.RedoOf is { } redo)
        {
            await RedoGridAsync(workspace, redo);
            return;
        }

        _ = question.DuringCommit ? await CommitAsync(workspace) : await FlushAsync(workspace);
    }

    /// <summary>
    /// The question to ask before <paramref name="operations"/> of the tab are written: null if none changes a primary key, all
    /// such changes were confirmed, or every relationship into the changed columns counted no rows. Counts on the workspace's
    /// session, in its transaction – what was written there already counts.
    /// </summary>
    private async Task<KeyChangeQuestion?> KeyQuestionAsync(
        WorkspaceTabs workspace, TableTab tab, IReadOnlyList<PendingOperation> operations, bool duringCommit, Guid? redoOf)
    {
        var id = workspace.WorkspaceId;
        var table = tab.Changes!.Table;
        var changes = KeyChanges.Of(table, operations).Where(o => !IsConfirmed(id, o)).ToList();
        var active = ScopeOf(id).Active;
        if (changes.Count == 0 || active.Schema is not { } schema)
        {
            return null;
        }

        var jumps = KeyChanges.References(table, changes, schema.IncomingOf(table.Table.Ref));
        if (jumps.Count == 0)
        {
            return null;
        }

        var data = await WorkspacesOf(id).GetDataAsync(id, CancellationToken.None);
        var references = new List<KeyReference>();
        var writes = RunningOn(id).Where(w => !w.Statement).ToList();
        SetCounting(writes, true);
        try
        {
            // One budget for all relationships (review of `small-pk-edit`, 4): unindexed columns over the VPN, one after the
            // other, held the write for 5 s each. What is left over is shown as not counted.
            var deadline = _time.GetUtcNow() + KeyCountBudget;
            foreach (var jump in jumps)
            {
                var left = deadline - _time.GetUtcNow();
                var count = left <= TimeSpan.Zero
                    ? new FkCount.Failed(TextFormat.Format(GridText.KeyChange_BudgetSpent, KeyCountBudget.TotalSeconds))
                    : await CountAsync(schema, active, data, jump, left < FkNavigation.CountTimeout ? left : FkNavigation.CountTimeout);
                if (count is not FkCount.Done { Count: 0 })
                {
                    references.Add(new KeyReference(jump, FkTargets.Label(active, jump.Table), count));
                }
            }
        }
        finally
        {
            SetCounting(writes, false);
        }

        return references.Count == 0
            ? null
            : new KeyChangeQuestion(id, tab, [.. changes.Select(o => o.Change.Id)], references, duringCommit)
            {
                RedoOf = redoOf,
                Confirms = changes.ToDictionary(o => o.Change.Id, o => (IReadOnlyDictionary<int, object?>)new Dictionary<int, object?>(o.Values)),
            };
    }

    /// <summary>
    /// One relationship's rows. Whatever keeps it from being counted – Oracle refuses (no rights), the relationship no longer
    /// fits the table (a column renamed: the filter check refuses), a value that does not fit the column – is shown, not
    /// thrown: the user decides (review of `small-pk-edit`, 1: a refused filter stopped every write). Only a cancel and a lost
    /// connection go on.
    /// </summary>
    private async Task<FkCount> CountAsync(SchemaCache schema, ActiveConnection active, IDataAccess data, FkJump jump, TimeSpan timeout)
    {
        if (jump.Unavailable is { } reason)
        {
            return new FkCount.Failed(reason);
        }

        try
        {
            var target = await schema.GetDetailsAsync(FkTargets.SummaryOf(active, jump.Table), CancellationToken.None);
            return await TimedQuery.RunAsync<long?>(async ct => await data.CountAsync(target, jump.Filters, ct), timeout, CancellationToken.None, _time) is { } rows
                ? new FkCount.Done(rows)
                : new FkCount.TimedOut();
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not DatabaseException { IsConnectionLost: true })
        {
            return new FkCount.Failed(ex.Message);
        }
    }

    /// <summary>How long counting the rows behind changed keys may take in all, before writing (the user's decision).</summary>
    public static readonly TimeSpan KeyCountBudget = TimeSpan.FromSeconds(10);

    /// <summary>The write counts (or no longer counts) the rows behind changed keys; afterwards it runs anew for the stuck check.</summary>
    private void SetCounting(IReadOnlyList<Work> writes, bool counting)
    {
        var now = _time.GetUtcNow();
        foreach (var work in writes)
        {
            work.Counting = counting;
            work.Started = now;
        }

        shell.NotifyChanged();
    }

    private Dictionary<Guid, IReadOnlyDictionary<int, object?>> KeysConfirmed(Guid workspaceId) =>
        _keysConfirmed.TryGetValue(workspaceId, out var set) ? set : _keysConfirmed[workspaceId] = [];

    /// <summary>
    /// The user chose to write exactly these values of the row anyway. A confirmation that outlived its write (the commit was
    /// refused before writing) does not let another value through unasked (review of `small-pk-edit`, 7).
    /// </summary>
    private bool IsConfirmed(Guid workspaceId, PendingOperation operation) =>
        KeysConfirmed(workspaceId).TryGetValue(operation.Change.Id, out var values)
        && values.Count == operation.Values.Count
        && operation.Values.All(v => values.TryGetValue(v.Key, out var confirmed) && Core.Oracle.OracleTypeMapper.ValuesEqual(confirmed, v.Value));

    /// <summary>
    /// What the user decided for this attempt to write – "overwrite" a row, "write anyway" a changed key – no longer holds: a
    /// later write checks again (review 3.31.0, A13).
    /// </summary>
    private void ClearDecisions(Guid workspaceId)
    {
        Overwrites(workspaceId).Clear();
        KeysConfirmed(workspaceId).Clear();
    }
}
