using FerretSharp.Core.Oracle;
using FerretSharp.Core.Resources;
using FerretSharp.Core.Schema;

namespace FerretSharp.Core.Data;

/// <summary>
/// Where a change is (docs/architecture.md 1.6): <see cref="Pending"/> only in the tab, <see cref="Flushed"/> executed in the
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

    // Whole values read later than the row (LOB editor): the reference for the concurrency check instead of the
    // preview the row was loaded with.
    private readonly Dictionary<int, object?> _loaded = [];

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

    /// <summary>What a write of the pending change would do; null if nothing is pending.</summary>
    public OperationKind? PendingKind =>
        !HasPending ? null
        : Deleted == ChangeStage.Pending ? OperationKind.Delete
        : IsNew && !IsInserted ? OperationKind.Insert
        : OperationKind.Update;

    /// <summary>Current value: pending, else flushed, else as loaded.</summary>
    public object? ValueOf(int column) =>
        _pending.TryGetValue(column, out var pending) ? pending
        : _flushed.TryGetValue(column, out var flushed) ? flushed
        : Original?[column];

    /// <summary>What the database holds in the workspace's transaction (before the pending change).</summary>
    public object? ExpectedOf(int column) =>
        _flushed.TryGetValue(column, out var flushed) ? flushed
        : _loaded.TryGetValue(column, out var loaded) ? loaded
        : Original?[column];

    /// <summary>The whole value of a column as read from the database in the workspace's transaction (LOB editor).</summary>
    internal void SetLoaded(int column, object? value) => _loaded[column] = value;

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

    internal void DiscardPending(int column) => _pending.Remove(column);

    /// <summary>Back to a pending state of the edit history (WP-30); flushed values stay as they are.</summary>
    internal void RestorePending(IReadOnlyDictionary<int, object?> pending, ChangeStage? deleted)
    {
        _pending.Clear();
        foreach (var (column, value) in pending)
        {
            _pending[column] = value;
        }

        Deleted = deleted;
    }

    /// <param name="written">
    /// The values an UPDATE or INSERT wrote; null for a delete (everything pending). A redo writes only its own columns – a
    /// newer value of one of them, or a pending delete, stays pending (R4a); so does a value typed while the write ran
    /// (review 3.31.0, A3).
    /// </param>
    internal void MarkFlushed(IReadOnlyDictionary<int, object?>? written, RowKey? newKey)
    {
        foreach (var (column, value) in written ?? _pending)
        {
            _flushed[column] = value;
        }

        if (written is null)
        {
            _pending.Clear();
            if (Deleted == ChangeStage.Pending)
            {
                Deleted = ChangeStage.Flushed;
            }
        }
        else
        {
            foreach (var (column, value) in written)
            {
                if (_pending.TryGetValue(column, out var pending) && OracleTypeMapper.ValuesEqual(pending, value))
                {
                    _pending.Remove(column);
                }
            }
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

        // A delete marked after the write stays marked (R4a: undoing the write took it back silently).
        Deleted = kind == OperationKind.Delete || Deleted == ChangeStage.Pending ? ChangeStage.Pending : before.Deleted;
        IsInserted = before.IsInserted;
        Key = before.Key;
    }

    internal sealed record Snapshot(IReadOnlyDictionary<int, object?> Flushed, ChangeStage? Deleted, bool IsInserted, RowKey Key);
}

/// <summary>What one flush changed in a tracker; <see cref="ChangeTracker.UndoFlush"/> turns it back into pending changes.</summary>
public sealed class FlushBatch
{
    internal List<(RowChange Change, OperationKind Kind, RowChange.Snapshot Before, IReadOnlyDictionary<int, object?> Values)> Items { get; } = [];

    /// <summary>The rows written, in the order written (jump to a row from the change overview, WP-30).</summary>
    public IReadOnlyList<RowChange> Rows => Items.Select(i => i.Change).ToList();
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
    IReadOnlyDictionary<int, object?> Expected)
{
    /// <summary>
    /// An insert's pending values when it was taken, also the NULLs it leaves out (defaults apply): what it writes. A value
    /// typed while it runs stays pending (review 3.31.0, A3).
    /// </summary>
    public IReadOnlyDictionary<int, object?>? Covers { get; init; }

    /// <summary>The values this operation writes: an update's and an insert's; null for a delete.</summary>
    internal IReadOnlyDictionary<int, object?>? Written => Kind switch
    {
        OperationKind.Update => Values,
        OperationKind.Insert => Covers ?? Values,
        _ => null,
    };
}

/// <summary>Result of an edit; <see cref="Error"/> is shown at the cell.</summary>
public sealed record EditResult(RowChange? Change, string? Error)
{
    public bool IsValid => Error is null;
}

/// <summary>
/// Changes of one tab (docs/architecture.md 1.6): pending until written, then flushed until commit. Existing rows are tracked by
/// row key, so the changes survive reloading (F5, filters) and are laid over the fresh rows again. The pending edits keep a
/// history (WP-30): <see cref="UndoEdit"/>/<see cref="RedoEdit"/> step through it cell by cell; a write ends it.
/// </summary>
public sealed class ChangeTracker(TableDetails table)
{
    private readonly Dictionary<RowKey, RowChange> _byKey = [];
    private readonly List<RowChange> _newRows = [];
    private readonly EditHistory _edits = new();

    /// <summary>
    /// The pending state of the rows of the writes taken back, as the undo left them and each redo since: a redo needs
    /// them exactly so (<see cref="IsPendingAsUndone"/>). Other rows of the tab may change meanwhile.
    /// </summary>
    private readonly Dictionary<RowChange, RowState> _asUndone = [];

    public TableDetails Table { get; } = table;

    /// <summary>New rows that are not inserted yet, in the order they were added.</summary>
    public IReadOnlyList<RowChange> NewRows => _newRows.Where(r => !r.IsInserted).ToList();

    public IEnumerable<RowChange> Changes => _byKey.Values.Concat(_newRows.Where(r => !r.IsInserted));

    public int PendingCount => Changes.Count(c => c.HasPending);

    public int FlushedCount => Changes.Count(c => c.HasFlushed);

    /// <summary>
    /// Rows whose pending changes the database does not hold yet for these columns (WP-58: a read of all values misses them):
    /// a pending value in one of them, a pending delete, a new row not inserted.
    /// </summary>
    public int PendingIn(IReadOnlyCollection<int> columns) => Changes.Count(c =>
        c.Deleted == ChangeStage.Pending || (c.IsNew && !c.IsInserted && c.Deleted is null) || c.PendingColumns.Any(columns.Contains));

    public bool HasChanges => _byKey.Count > 0 || _newRows.Count > 0;

    /// <summary>A pending edit can be taken back (tests).</summary>
    internal bool CanUndoEdit => _edits.CanUndo;

    /// <summary>An edit taken back can be applied again – until the next edit or write (tests).</summary>
    internal bool CanRedoEdit => _edits.CanRedo;

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

        if (OracleTypeMapper.IsLob(info))
        {
            return new EditResult(null, TextFormat.Format(DataText.EditInLobEditor, info.DisplayType));
        }

        if (!OracleTypeMapper.IsEditableValue(row.Values[column]))
        {
            return new EditResult(null, DataText.NumberTooPreciseToEdit);
        }

        if (existing?.Deleted is not null)
        {
            return new EditResult(null, DataText.RowMarkedForDeletion);
        }

        var parsed = OracleTypeMapper.Parse(info, text);
        if (!parsed.IsValid)
        {
            return new EditResult(null, parsed.Error);
        }

        var before = existing is null ? null : State(existing);
        var change = existing ?? new RowChange(row.Key, row.Values);
        change.Set(column, parsed.Value, OracleTypeMapper.ValuesEqual(parsed.Value, change.ExpectedOf(column)));
        Keep(change);
        Remember([before ?? RowState.Untouched(change)]);
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

        var before = State(newRow);
        newRow.Set(column, parsed.IsValid ? parsed.Value : null, equalsExpected: false);
        Remember([before]);
        return new EditResult(newRow, null);
    }

    /// <summary>
    /// Sets the whole value of a LOB column of a loaded row (LOB editor, WP-10). <paramref name="loaded"/> is the whole
    /// value the editor read from the database: the concurrency check compares against it, not against the preview.
    /// </summary>
    public EditResult SetContent(RowData row, int column, object? loaded, object? value)
    {
        var info = Table.Columns[column];
        if (!OracleTypeMapper.IsLob(info))
        {
            return new EditResult(null, TextFormat.Format(DataText.NotALobColumn, info.Name));
        }

        if (OracleTypeMapper.NotEditableReason(Table, info, newRow: false) is { } reason)
        {
            return new EditResult(null, reason);
        }

        var existing = _byKey.GetValueOrDefault(row.Key);
        if (existing?.Deleted is not null)
        {
            return new EditResult(null, DataText.RowMarkedForDeletion);
        }

        var checkedValue = OracleTypeMapper.CheckContent(info, value);
        if (!checkedValue.IsValid)
        {
            return new EditResult(null, checkedValue.Error);
        }

        var before = existing is null ? null : State(existing);
        var change = existing ?? new RowChange(row.Key, row.Values);
        change.SetLoaded(column, loaded);
        change.Set(column, checkedValue.Value, OracleTypeMapper.ValuesEqual(checkedValue.Value, change.ExpectedOf(column)));
        Keep(change);
        Remember([before ?? RowState.Untouched(change)]);
        return new EditResult(change, null);
    }

    /// <summary>Sets the whole value of a LOB column of a new row that is not inserted yet.</summary>
    public EditResult SetContent(RowChange newRow, int column, object? value)
    {
        var info = Table.Columns[column];
        if (!OracleTypeMapper.IsLob(info))
        {
            return new EditResult(null, TextFormat.Format(DataText.NotALobColumn, info.Name));
        }

        if (OracleTypeMapper.NotEditableReason(Table, info, newRow: true) is { } reason)
        {
            return new EditResult(null, reason);
        }

        // Like SetValue on a new row: empty stays allowed, a missing NOT NULL value is reported by Oracle on insert.
        if (value is null or string { Length: 0 })
        {
            var cleared = State(newRow);
            newRow.Set(column, null, equalsExpected: false);
            Remember([cleared]);
            return new EditResult(newRow, null);
        }

        var checkedValue = OracleTypeMapper.CheckContent(info, value);
        if (!checkedValue.IsValid)
        {
            return new EditResult(null, checkedValue.Error);
        }

        var before = State(newRow);
        newRow.Set(column, checkedValue.Value, equalsExpected: false);
        Remember([before]);
        return new EditResult(newRow, null);
    }

    public RowChange AddRow()
    {
        var row = new RowChange(RowKey.None.Instance, null);
        _newRows.Add(row);
        Remember([RowState.Untouched(row)]);
        return row;
    }

    /// <summary>Marks a loaded row for deletion (pending); a new row that was never inserted simply disappears.</summary>
    public void Delete(RowData row) => Delete([row]);

    /// <summary>Marks loaded rows for deletion (Del on several rows): one step in the edit history (WP-59).</summary>
    public void Delete(IReadOnlyList<RowData> rows)
    {
        var before = new List<RowState>(rows.Count);
        foreach (var row in rows.DistinctBy(r => r.Key))
        {
            var existing = _byKey.GetValueOrDefault(row.Key);
            var change = existing ?? new RowChange(row.Key, row.Values);
            before.Add(existing is null ? RowState.Untouched(change) : State(existing));
            change.DiscardPending();
            change.Deleted ??= ChangeStage.Pending;
            Keep(change);
        }

        Remember(before);
    }

    /// <summary>At most this many rows per command for many cells (WP-59): writing them is one UPDATE each over the VPN.</summary>
    public const int MaxFillRows = 10_000;

    /// <summary>
    /// Sets many cells of loaded rows at once (WP-59): one value, NULL, or the top row's values down. Each column is checked
    /// once – a value invalid for one of them sets nothing. Columns that cannot take the value are left as they are and
    /// named, so are rows marked for deletion and numbers too precise to edit here. One step in the edit history.
    /// </summary>
    /// <param name="rows">The rows top to bottom, as loaded (a fill down takes the first one's values, as shown).</param>
    public CellFillResult SetCells(IReadOnlyList<RowData> rows, IReadOnlyList<int> columns, CellFill fill)
    {
        if (FillRefusal(rows.Count, fill) is { } refusal)
        {
            return CellFillResult.Failed(refusal);
        }

        if (rows.Count == 0)
        {
            return new CellFillResult(0, [], 0, 0, null); // an empty table, or a read that found none of the rows any more
        }

        var values = new List<(int Column, object? Value)>(columns.Count);
        var skipped = new List<SkippedColumn>();
        foreach (var column in columns)
        {
            var info = Table.Columns[column];
            var (value, skip, error) = FillValueOf(rows[0], column, fill);
            if (error is not null)
            {
                return CellFillResult.Failed(columns.Count > 1 ? TextFormat.Format(DataText.FillInvalidInColumn, info.Name, error) : error);
            }

            if (skip is not null)
            {
                skipped.Add(new SkippedColumn(column, info.Name, skip));
            }
            else
            {
                values.Add((column, value));
            }
        }

        var before = new List<RowState>();
        var changed = new List<RowKey>();
        var seen = new HashSet<RowKey>();
        var (cells, deleted, tooPrecise) = (0, 0, 0);
        foreach (var row in values.Count == 0 ? [] : fill.Kind == CellFillKind.Down ? rows.Skip(1) : rows)
        {
            if (!seen.Add(row.Key))
            {
                continue;
            }

            var existing = _byKey.GetValueOrDefault(row.Key);
            if (existing?.Deleted is not null)
            {
                deleted++;
                continue;
            }

            var change = existing ?? new RowChange(row.Key, row.Values);
            var state = existing is null ? RowState.Untouched(change) : State(existing);
            var touched = false;
            foreach (var (column, value) in values)
            {
                if (!OracleTypeMapper.IsEditableValue(row.Values[column]))
                {
                    tooPrecise++;
                    continue;
                }

                change.Set(column, value, OracleTypeMapper.ValuesEqual(value, change.ExpectedOf(column)));
                cells++;
                touched = true;
            }

            if (touched)
            {
                before.Add(state);
                changed.Add(row.Key);
                Keep(change);
            }
        }

        Remember(before);
        return new CellFillResult(cells, skipped, deleted, tooPrecise, null) { Changed = changed };
    }

    /// <summary>
    /// Why a command for many cells cannot run on so many rows (too many; a fill down of one); null if it can. The grid asks
    /// before it reads rows that are not loaded.
    /// </summary>
    public static string? FillRefusal(int rows, CellFill fill) =>
        rows > MaxFillRows ? TextFormat.Format(DataText.FillTooManyRows, MaxFillRows)
        : fill.Kind == CellFillKind.Down && rows < 2 ? DataText.FillDownNeedsTwoRows
        : null;

    /// <summary>
    /// Why a column of loaded rows cannot take a value of a command for many cells (WP-59: not editable, a LOB); null if it
    /// can. The cell menu asks it too, to grey out "Set value…".
    /// </summary>
    public static string? FillUnavailable(TableDetails table, ColumnInfo column) =>
        OracleTypeMapper.NotEditableReason(table, column, newRow: false)
        ?? (OracleTypeMapper.IsLob(column) ? TextFormat.Format(DataText.EditInLobEditor, column.DisplayType) : null);

    /// <summary>
    /// What a command for many cells puts into one column: the value, or why the column is left as it is (skip), or why
    /// nothing may be set at all (error: the typed value does not fit the column).
    /// </summary>
    private (object? Value, string? Skip, string? Error) FillValueOf(RowData top, int column, CellFill fill)
    {
        var info = Table.Columns[column];
        if (FillUnavailable(Table, info) is { } reason)
        {
            return (null, reason, null);
        }

        switch (fill.Kind)
        {
            case CellFillKind.Value:
                var parsed = OracleTypeMapper.Parse(info, fill.Text);
                return parsed.IsValid ? (parsed.Value, null, null) : (null, null, parsed.Error);
            case CellFillKind.Null:
                return info.Nullable ? (null, null, null) : (null, TextFormat.Format(OracleText.ValueRequired, info.Name), null);
            default:
                // As shown (A21): a pending value of the top row goes down.
                var value = Find(top.Key) is { } change ? change.ValueOf(column) : top.Values[column];
                if (!OracleTypeMapper.IsEditableValue(value))
                {
                    return (null, DataText.NumberTooPreciseToEdit, null);
                }

                return value is null && !info.Nullable ? (null, TextFormat.Format(OracleText.ValueRequired, info.Name), null) : (value, null, null);
        }
    }

    public void Delete(RowChange newRow)
    {
        if (newRow.IsNew && !newRow.IsInserted)
        {
            var before = State(newRow);
            _newRows.Remove(newRow);
            Remember([before]);
        }
    }

    /// <summary>Undoes the pending changes of one row (also a pending delete).</summary>
    public void Revert(RowKey key) => Revert([key]);

    /// <summary>Undoes the pending changes of rows (also pending deletes): one step in the edit history (WP-59).</summary>
    public void Revert(IReadOnlyList<RowKey> keys)
    {
        var before = new List<RowState>(keys.Count);
        foreach (var key in keys.Distinct())
        {
            if (_byKey.GetValueOrDefault(key) is { } change)
            {
                before.Add(State(change));
                change.DiscardPending();
                Forget(change);
            }
        }

        Remember(before);
    }

    /// <summary>Undoes the pending value of one cell (change overview, WP-30); a pending delete stays.</summary>
    public void Revert(RowChange change, int column)
    {
        if (!change.PendingColumns.Contains(column))
        {
            return;
        }

        var before = State(change);
        change.DiscardPending(column);
        if (!change.IsNew || change.IsInserted)
        {
            Forget(change);
        }

        Remember([before]);
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
            operations.Add(InsertOf(change));
        }

        return operations;
    }

    /// <summary>Only the filled values are inserted; the NULLs are covered too (defaults apply to them).</summary>
    private static PendingOperation InsertOf(RowChange change) =>
        new(change, OperationKind.Insert, RowKey.None.Instance,
            change.PendingValues.Where(v => v.Value is not null).ToDictionary(v => v.Key, v => v.Value), new Dictionary<int, object?>())
        {
            Covers = new Dictionary<int, object?>(change.PendingValues),
        };

    /// <summary>
    /// The pending operations of the rows of a write taken back (redo, WP-30) – in the order of <see cref="PendingOperations()"/>,
    /// without other pending rows of the tab.
    /// </summary>
    public IReadOnlyList<PendingOperation> PendingOperations(FlushBatch batch)
    {
        // The write's own values, not all that is pending: a later write taken back with it may have pending values in the
        // same rows – they wait for their own redo (R4a).
        var operations = new List<PendingOperation>();
        foreach (var (change, _, _, _) in batch.Items.Where(i => i.Kind == OperationKind.Delete))
        {
            operations.Add(new PendingOperation(change, OperationKind.Delete, change.Key, new Dictionary<int, object?>(), new Dictionary<int, object?>()));
        }

        foreach (var (change, _, _, values) in batch.Items.Where(i => i.Kind == OperationKind.Update))
        {
            var expected = change.IsNew ? new Dictionary<int, object?>() : values.Keys.ToDictionary(c => c, change.ExpectedOf);
            operations.Add(new PendingOperation(change, OperationKind.Update, change.Key, new Dictionary<int, object?>(values), expected));
        }

        foreach (var (change, _, _, _) in batch.Items.Where(i => i.Kind == OperationKind.Insert))
        {
            operations.Add(InsertOf(change));
        }

        return operations;
    }

    /// <summary>
    /// The changes of a write taken back are pending as the undo left them (WP-30): nothing edited, discarded or added in
    /// the tab since (or taken back again with Ctrl+Z) – only then can the write be applied again as it was. Compared for
    /// the whole tab, not per write (R4a): two writes of one row taken back together leave both their values pending,
    /// which matched neither.
    /// </summary>
    public bool IsPendingAsUndone(FlushBatch batch) => _asUndone.All(p => p.Value.Same(State(p.Key))) && batch.Items.All(item => item.Kind switch
    {
        OperationKind.Delete => item.Change.Deleted == ChangeStage.Pending && IsTracked(item.Change),
        OperationKind.Insert => !item.Change.IsInserted && _newRows.Contains(item.Change),
        _ => IsTracked(item.Change),
    });

    /// <summary>After a successful flush: pending becomes flushed; inserted rows get the key Oracle gave them.</summary>
    /// <param name="newKeys">Key of each inserted row (from <see cref="PendingOperation.Change"/>).</param>
    /// <returns>What changed, for <see cref="UndoFlush"/> after the transaction went back to the flush's savepoint.</returns>
    public FlushBatch MarkFlushed(IReadOnlyList<PendingOperation> operations, IReadOnlyDictionary<Guid, RowKey> newKeys)
    {
        var batch = new FlushBatch();
        foreach (var operation in operations)
        {
            var change = operation.Change;
            var written = operation.Written;
            batch.Items.Add((change, operation.Kind, change.Take(), new Dictionary<int, object?>(written ?? change.PendingValues)));
            change.MarkFlushed(written, newKeys.GetValueOrDefault(change.Id));
            if (operation.Kind == OperationKind.Insert)
            {
                _newRows.Remove(change);
                _byKey[change.Key] = change;
            }
        }

        ForgetEdits();
        RefreshAsUndone(); // a redo: the next write taken back is pending as this one left it
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

        ForgetEdits();
        foreach (var (change, _, _, _) in batch.Items)
        {
            _asUndone[change] = State(change);
        }

        RefreshAsUndone(); // a row also in a later write taken back just before
    }

    /// <summary>Drops pending changes; flushed ones stay (they are in the transaction).</summary>
    public void DiscardPending()
    {
        var before = Changes.Where(c => c.HasPending).Select(State).ToList();
        _newRows.RemoveAll(r => !r.IsInserted);
        foreach (var change in _byKey.Values.ToList())
        {
            change.DiscardPending();
            Forget(change);
        }

        Remember(before);
    }

    /// <summary>After commit or rollback: nothing is pending or flushed any more.</summary>
    public void Clear()
    {
        _byKey.Clear();
        _newRows.Clear();
        ForgetEdits();
        _asUndone.Clear();
    }

    /// <summary>Takes back the last pending edit (Ctrl+Z in the grid).</summary>
    /// <returns>The rows it touched; empty if there was nothing to take back.</returns>
    public IReadOnlyList<RowChange> UndoEdit() => Restore(_edits.Undo());

    /// <summary>Applies the last edit taken back again (Ctrl+Y in the grid).</summary>
    /// <returns>The rows it touched; empty if there was nothing to apply again.</returns>
    public IReadOnlyList<RowChange> RedoEdit() => Restore(_edits.Redo());

    private IReadOnlyList<RowChange> Restore(IReadOnlyList<RowState> states)
    {
        // Several rows (discard of the whole tab): new rows go back to their places in order.
        foreach (var state in states.OrderBy(s => s.NewRowIndex))
        {
            Apply(state);
        }

        return states.Select(s => s.Change).ToList();
    }

    private void Apply(RowState state)
    {
        var change = state.Change;
        change.RestorePending(state.Pending, state.Deleted);
        if (change.IsNew && !change.IsInserted)
        {
            _newRows.Remove(change);
            if (state.NewRowIndex >= 0)
            {
                _newRows.Insert(Math.Min(state.NewRowIndex, _newRows.Count), change);
            }
        }
        else
        {
            Keep(change);
        }
    }

    private RowState State(RowChange change) =>
        new(change, new Dictionary<int, object?>(change.PendingValues), change.Deleted, _newRows.IndexOf(change));

    /// <summary>One edit done: its step goes on the history (if it changed anything) and ends redo.</summary>
    private void Remember(IReadOnlyList<RowState> before) => _edits.Remember(before, State);

    /// <summary>A write, its undo, commit or rollback: the pending state the history describes is gone.</summary>
    private void ForgetEdits() => _edits.Clear();

    private void RefreshAsUndone()
    {
        foreach (var change in _asUndone.Keys.ToList())
        {
            _asUndone[change] = State(change);
        }
    }

    private bool IsTracked(RowChange change) => _byKey.TryGetValue(change.Key, out var tracked) && ReferenceEquals(tracked, change);

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
