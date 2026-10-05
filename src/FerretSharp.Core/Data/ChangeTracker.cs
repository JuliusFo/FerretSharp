using FerretSharp.Core.Oracle;
using FerretSharp.Core.Schema;

namespace FerretSharp.Core.Data;

/// <summary>
/// Where a change is (CLAUDE.md 5.6): <see cref="Pending"/> only in the tab, <see cref="Flushed"/> executed in the
/// workspace's transaction (rows locked, invisible to others until commit).
/// </summary>
public enum ChangeStage
{
    Pending,
    Flushed,
}

public enum OperationKind
{
    Insert,
    Update,
    Delete,
}

/// <summary>
/// Changes of one row. Existing rows are identified by their <see cref="Key"/>; new rows by <see cref="Id"/> until
/// they are inserted (then <see cref="Key"/> is the key Oracle gave them).
/// </summary>
public sealed class RowChange
{
    private readonly Dictionary<int, object?> _pending = [];
    private readonly Dictionary<int, object?> _flushed = [];

    internal RowChange(RowKey key, IReadOnlyList<object?>? original)
    {
        Key = key;
        Original = original;
    }

    public Guid Id { get; } = Guid.NewGuid();

    /// <summary>Row key; <see cref="RowKey.None"/> for a new row that is not inserted yet.</summary>
    public RowKey Key { get; internal set; }

    /// <summary>Values as loaded; null for new rows.</summary>
    public IReadOnlyList<object?>? Original { get; }

    public bool IsNew => Original is null;

    /// <summary>A new row that has been inserted (flushed) in the workspace's transaction.</summary>
    public bool IsInserted { get; internal set; }

    /// <summary>Marked for deletion; null if not.</summary>
    public ChangeStage? Deleted { get; internal set; }

    /// <summary>Column indexes with a pending value.</summary>
    public IReadOnlyCollection<int> PendingColumns => _pending.Keys;

    public bool HasPending => Deleted == ChangeStage.Pending || _pending.Count > 0 || (IsNew && !IsInserted && Deleted is null);

    public bool HasFlushed => Deleted == ChangeStage.Flushed || _flushed.Count > 0 || IsInserted;

    /// <summary>Current value: pending, else flushed, else as loaded.</summary>
    public object? ValueOf(int column) =>
        _pending.TryGetValue(column, out var pending) ? pending
        : _flushed.TryGetValue(column, out var flushed) ? flushed
        : Original?[column];

    /// <summary>What the database holds in the workspace's transaction (before the pending change).</summary>
    public object? ExpectedOf(int column) => _flushed.TryGetValue(column, out var flushed) ? flushed : Original?[column];

    public ChangeStage? StageOf(int column) =>
        _pending.ContainsKey(column) ? ChangeStage.Pending
        : _flushed.ContainsKey(column) || IsInserted ? ChangeStage.Flushed
        : IsNew ? ChangeStage.Pending
        : null;

    internal void Set(int column, object? value, bool equalsExpected)
    {
        if (equalsExpected && !(IsNew && !IsInserted))
        {
            _pending.Remove(column);
        }
        else
        {
            _pending[column] = value;
        }
    }

    internal void DiscardPending()
    {
        _pending.Clear();
        if (Deleted == ChangeStage.Pending)
        {
            Deleted = null;
        }
    }

    internal void MarkFlushed(RowKey? newKey)
    {
        foreach (var (column, value) in _pending)
        {
            _flushed[column] = value;
        }

        _pending.Clear();
        if (Deleted == ChangeStage.Pending)
        {
            Deleted = ChangeStage.Flushed;
        }

        if (IsNew && !IsInserted)
        {
            IsInserted = true;
        }

        if (newKey is not null)
        {
            Key = newKey;
        }
    }

    internal IReadOnlyDictionary<int, object?> PendingValues => _pending;

    internal Snapshot Take() => new(new Dictionary<int, object?>(_flushed), Deleted, IsInserted, Key);

    /// <summary>
    /// Undo of a flush: back to the state before it, with the flushed values pending again – unless the cell was
    /// edited since, then the newer pending value stays.
    /// </summary>
    internal void Restore(Snapshot before, IReadOnlyDictionary<int, object?> flushedValues, OperationKind kind)
    {
        _flushed.Clear();
        foreach (var (column, value) in before.Flushed)
        {
            _flushed[column] = value;
        }

        foreach (var (column, value) in flushedValues)
        {
            _pending.TryAdd(column, value);
        }

        Deleted = kind == OperationKind.Delete ? ChangeStage.Pending : before.Deleted;
        IsInserted = before.IsInserted;
        Key = before.Key;
    }

    internal sealed record Snapshot(IReadOnlyDictionary<int, object?> Flushed, ChangeStage? Deleted, bool IsInserted, RowKey Key);
}

/// <summary>What one flush changed in a tracker; <see cref="ChangeTracker.UndoFlush"/> turns it back into pending changes.</summary>
public sealed class FlushBatch
{
    internal List<(RowChange Change, OperationKind Kind, RowChange.Snapshot Before, IReadOnlyDictionary<int, object?> Values)> Items { get; } = [];
}

/// <summary>A pending change as it will be written: the input for the DML.</summary>
/// <param name="Values">New values by column index (INSERT: all filled columns, UPDATE: changed columns).</param>
/// <param name="Expected">UPDATE: what the changed columns must still hold in the database (concurrency check);
/// empty for rows inserted in this transaction.</param>
public sealed record PendingOperation(
    RowChange Change,
    OperationKind Kind,
    RowKey Key,
    IReadOnlyDictionary<int, object?> Values,
    IReadOnlyDictionary<int, object?> Expected);

/// <summary>Result of an edit; <see cref="Error"/> is shown at the cell.</summary>
public sealed record EditResult(RowChange? Change, string? Error)
{
    public bool IsValid => Error is null;
}

/// <summary>
/// Changes of one tab (CLAUDE.md 5.6): pending until written, then flushed until commit. Existing rows are tracked by
/// row key, so the changes survive reloading (F5, filters) and are laid over the fresh rows again.
/// </summary>
public sealed class ChangeTracker(TableDetails table)
{
    private readonly Dictionary<RowKey, RowChange> _byKey = [];
    private readonly List<RowChange> _newRows = [];

    public TableDetails Table { get; } = table;

    /// <summary>New rows that are not inserted yet, in the order they were added.</summary>
    public IReadOnlyList<RowChange> NewRows => _newRows.Where(r => !r.IsInserted).ToList();

    public IEnumerable<RowChange> Changes => _byKey.Values.Concat(_newRows.Where(r => !r.IsInserted));

    public int PendingCount => Changes.Count(c => c.HasPending);

    public int FlushedCount => Changes.Count(c => c.HasFlushed);

    public bool HasChanges => _byKey.Count > 0 || _newRows.Count > 0;

    public RowChange? Find(RowKey key) => _byKey.GetValueOrDefault(key);

    /// <summary>Edit a cell of a loaded row.</summary>
    public EditResult SetValue(RowData row, int column, string text)
    {
        var info = Table.Columns[column];
        var existing = _byKey.GetValueOrDefault(row.Key);
        // A loaded row exists in the database (also one inserted in this transaction): its key stays as it is.
        if (OracleTypeMapper.NotEditableReason(Table, info, newRow: false) is { } reason)
        {
            return new EditResult(null, reason);
        }

        if (!OracleTypeMapper.IsEditableValue(row.Values[column]))
        {
            return new EditResult(null, "Zahlen mit mehr als 28 Stellen lassen sich hier nicht bearbeiten.");
        }

        if (existing?.Deleted is not null)
        {
            return new EditResult(null, "Die Zeile ist zum Löschen markiert.");
        }

        var parsed = OracleTypeMapper.Parse(info, text);
        if (!parsed.IsValid)
        {
            return new EditResult(null, parsed.Error);
        }

        var change = existing ?? new RowChange(row.Key, row.Values);
        change.Set(column, parsed.Value, OracleTypeMapper.ValuesEqual(parsed.Value, change.ExpectedOf(column)));
        Keep(change);
        return new EditResult(change, null);
    }

    /// <summary>Edit a cell of a new row that is not inserted yet.</summary>
    public EditResult SetValue(RowChange newRow, int column, string text)
    {
        var info = Table.Columns[column];
        if (OracleTypeMapper.NotEditableReason(Table, info, newRow: true) is { } reason)
        {
            return new EditResult(null, reason);
        }

        // Empty stays allowed while filling a new row; a missing NOT NULL value is reported by Oracle on insert.
        var parsed = OracleTypeMapper.Parse(info, text);
        if (!parsed.IsValid && !string.IsNullOrEmpty(text))
        {
            return new EditResult(null, parsed.Error);
        }

        newRow.Set(column, parsed.IsValid ? parsed.Value : null, equalsExpected: false);
        return new EditResult(newRow, null);
    }

    public RowChange AddRow()
    {
        var row = new RowChange(RowKey.None.Instance, null);
        _newRows.Add(row);
        return row;
    }

    /// <summary>Marks a loaded row for deletion (pending); a new row that was never inserted simply disappears.</summary>
    public void Delete(RowData row)
    {
        var change = _byKey.GetValueOrDefault(row.Key) ?? new RowChange(row.Key, row.Values);
        change.DiscardPending();
        change.Deleted ??= ChangeStage.Pending;
        Keep(change);
    }

    public void Delete(RowChange newRow)
    {
        if (newRow.IsNew && !newRow.IsInserted)
        {
            _newRows.Remove(newRow);
        }
    }

    /// <summary>Undoes the pending changes of one row (also a pending delete).</summary>
    public void Revert(RowKey key)
    {
        if (_byKey.GetValueOrDefault(key) is { } change)
        {
            change.DiscardPending();
            Forget(change);
        }
    }

    /// <summary>Everything pending, in an order Oracle accepts: deletes, updates, then inserts.</summary>
    public IReadOnlyList<PendingOperation> PendingOperations()
    {
        var operations = new List<PendingOperation>();
        foreach (var change in _byKey.Values.Where(c => c.Deleted == ChangeStage.Pending))
        {
            operations.Add(new PendingOperation(change, OperationKind.Delete, change.Key, new Dictionary<int, object?>(), new Dictionary<int, object?>()));
        }

        foreach (var change in _byKey.Values.Where(c => c.Deleted is null && c.PendingValues.Count > 0))
        {
            var expected = change.IsNew
                ? new Dictionary<int, object?>()
                : change.PendingColumns.ToDictionary(c => c, change.ExpectedOf);
            operations.Add(new PendingOperation(change, OperationKind.Update, change.Key, new Dictionary<int, object?>(change.PendingValues), expected));
        }

        foreach (var change in _newRows.Where(c => !c.IsInserted))
        {
            var values = change.PendingValues.Where(v => v.Value is not null).ToDictionary(v => v.Key, v => v.Value);
            operations.Add(new PendingOperation(change, OperationKind.Insert, RowKey.None.Instance, values, new Dictionary<int, object?>()));
        }

        return operations;
    }

    /// <summary>After a successful flush: pending becomes flushed; inserted rows get the key Oracle gave them.</summary>
    /// <param name="newKeys">Key of each inserted row (from <see cref="PendingOperation.Change"/>).</param>
    /// <returns>What changed, for <see cref="UndoFlush"/> after the transaction went back to the flush's savepoint.</returns>
    public FlushBatch MarkFlushed(IReadOnlyList<PendingOperation> operations, IReadOnlyDictionary<Guid, RowKey> newKeys)
    {
        var batch = new FlushBatch();
        foreach (var operation in operations)
        {
            var change = operation.Change;
            batch.Items.Add((change, operation.Kind, change.Take(), new Dictionary<int, object?>(change.PendingValues)));
            change.MarkFlushed(newKeys.GetValueOrDefault(change.Id));
            if (operation.Kind == OperationKind.Insert)
            {
                _newRows.Remove(change);
                _byKey[change.Key] = change;
            }
        }

        return batch;
    }

    /// <summary>The flush was rolled back (to its savepoint): its changes are pending again.</summary>
    public void UndoFlush(FlushBatch batch)
    {
        foreach (var (change, kind, before, values) in Enumerable.Reverse(batch.Items))
        {
            if (kind == OperationKind.Insert)
            {
                _byKey.Remove(change.Key);
            }

            change.Restore(before, values, kind);
            if (kind == OperationKind.Insert)
            {
                _newRows.Add(change);
            }
            else
            {
                Keep(change);
            }
        }
    }

    /// <summary>Drops pending changes; flushed ones stay (they are in the transaction).</summary>
    public void DiscardPending()
    {
        _newRows.RemoveAll(r => !r.IsInserted);
        foreach (var change in _byKey.Values.ToList())
        {
            change.DiscardPending();
            Forget(change);
        }
    }

    /// <summary>After commit or rollback: nothing is pending or flushed any more.</summary>
    public void Clear()
    {
        _byKey.Clear();
        _newRows.Clear();
    }

    private void Keep(RowChange change)
    {
        if (change.HasPending || change.HasFlushed)
        {
            _byKey[change.Key] = change;
        }
        else
        {
            _byKey.Remove(change.Key);
        }
    }

    private void Forget(RowChange change)
    {
        if (!change.HasPending && !change.HasFlushed)
        {
            _byKey.Remove(change.Key);
        }
    }
}
