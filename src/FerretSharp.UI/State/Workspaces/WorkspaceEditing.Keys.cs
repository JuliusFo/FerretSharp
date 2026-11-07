using FerretSharp.Core.Connections;
using FerretSharp.Core.Data;
using FerretSharp.Core.Query;
using FerretSharp.Core.Schema;

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
}

/// <summary>Changed primary keys: count the rows still pointing to the old keys before writing (ADR 0007 addendum).</summary>
public sealed partial class WorkspaceEditing
{
    private readonly Dictionary<Guid, HashSet<Guid>> _keysConfirmed = [];

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

        KeysConfirmed(question.WorkspaceId).UnionWith(question.Changes);
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
        var changes = KeyChanges.Of(table, operations).Where(o => !KeysConfirmed(id).Contains(o.Change.Id)).ToList();
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
        foreach (var jump in jumps)
        {
            var count = await CountAsync(schema, active, data, jump);
            if (count is not FkCount.Done { Count: 0 })
            {
                references.Add(new KeyReference(jump, FkTargets.Label(active, jump.Table), count));
            }
        }

        return references.Count == 0
            ? null
            : new KeyChangeQuestion(id, tab, [.. changes.Select(o => o.Change.Id)], references, duringCommit) { RedoOf = redoOf };
    }

    /// <summary>One relationship's rows; a refusal of Oracle (no rights on the table …) is shown, not thrown – the user decides.</summary>
    private static async Task<FkCount> CountAsync(SchemaCache schema, ActiveConnection active, IDataAccess data, FkJump jump)
    {
        if (jump.Unavailable is { } reason)
        {
            return new FkCount.Failed(reason);
        }

        try
        {
            var target = await schema.GetDetailsAsync(FkTargets.SummaryOf(active, jump.Table), CancellationToken.None);
            return await FkNavigation.CountAsync(data, target, jump, FkNavigation.CountTimeout, CancellationToken.None) is { } rows
                ? new FkCount.Done(rows)
                : new FkCount.TimedOut();
        }
        catch (DatabaseException ex) when (!ex.IsConnectionLost)
        {
            return new FkCount.Failed(ex.Message);
        }
    }

    private HashSet<Guid> KeysConfirmed(Guid workspaceId) =>
        _keysConfirmed.TryGetValue(workspaceId, out var set) ? set : _keysConfirmed[workspaceId] = [];

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
