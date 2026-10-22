using FerretSharp.Core.Data;
using FerretSharp.Core.Resources;
using FerretSharp.Core.Schema;

namespace FerretSharp.Core.Relations;

/// <summary>What happens to a row whose key the target table has already (decision of the user: skip by default).</summary>
public enum ConflictMode
{
    /// <summary>Leave the row in the target as it is.</summary>
    Skip,

    /// <summary>Overwrite the target row with the source's values (only with the target's primary key as the key).</summary>
    Update,

    /// <summary>Do not copy anything while such a row exists.</summary>
    Abort,
}

public enum CopySeverity
{
    /// <summary>The copy cannot run.</summary>
    Error,

    /// <summary>The copy runs, but something is lost or may fail.</summary>
    Warning,

    /// <summary>Good to know (triggers fire along).</summary>
    Hint,
}

/// <param name="Table">The source table the issue belongs to; null for the copy as a whole (a cycle).</param>
public sealed record CopyIssue(CopySeverity Severity, TableRef? Table, string Message);

/// <summary>A source column copied into a target column, both by index.</summary>
public sealed record ColumnMapping(int Source, int Target);

/// <summary>One table of a copy: its rows in the order to insert them, where they go and which exist there already.</summary>
public sealed class TableCopy
{
    internal TableCopy(
        TableRows source, TableDetails? target, IReadOnlyList<ColumnMapping> columns, IReadOnlyList<RowData> rows,
        IReadOnlySet<RowKey> existing, IReadOnlyList<int>? conflictKey, IReadOnlyList<TableRef> triggers)
    {
        Source = source;
        Target = target;
        Columns = columns;
        Rows = rows;
        Existing = existing;
        ConflictKey = conflictKey;
        Triggers = triggers;
    }

    public TableRows Source { get; }

    public TableRef Table => Source.Table;

    /// <summary>The table in the target; null if it has none of that name.</summary>
    public TableDetails? Target { get; }

    public IReadOnlyList<ColumnMapping> Columns { get; }

    /// <summary>The source rows, a row before those of the same table that reference it (self reference).</summary>
    public IReadOnlyList<RowData> Rows { get; }

    /// <summary>Source rows (by their source key) whose key the target table holds already.</summary>
    public IReadOnlySet<RowKey> Existing { get; }

    /// <summary>The target columns that recognize an existing row: the enforced primary key, else a unique key; null without.</summary>
    public IReadOnlyList<int>? ConflictKey { get; }

    /// <summary>Overwriting needs the target's primary key as the key (UPDATE … WHERE primary key).</summary>
    public bool CanUpdate => ConflictKey is not null && Target is { PrimaryKeyEnforced: true } target
                             && ConflictKey.Select(i => target.Columns[i].Name).Order(StringComparer.Ordinal)
                                 .SequenceEqual(target.PrimaryKey.Order(StringComparer.Ordinal), StringComparer.Ordinal);

    /// <summary>Enabled triggers on the target table, owner and name.</summary>
    public IReadOnlyList<TableRef> Triggers { get; }

    public int NewRows => Rows.Count - Existing.Count;
}

/// <summary>
/// A copy of rows into a workspace of another connection, checked against the target before anything is written (WP-42,
/// <see cref="SubgraphCopyPlanner"/>): tables parents first, columns matched by name, existing rows found by key, and what
/// stands in the way.
/// </summary>
public sealed class SubgraphCopyPlan
{
    internal SubgraphCopyPlan(IReadOnlyList<TableCopy> tables, IReadOnlyList<CopyIssue> issues)
    {
        Tables = tables;
        Issues = issues;
    }

    /// <summary>In the order to insert them.</summary>
    public IReadOnlyList<TableCopy> Tables { get; }

    /// <summary>Errors first, then warnings and hints, each in the order of the tables.</summary>
    public IReadOnlyList<CopyIssue> Issues { get; }

    public int Rows => Tables.Sum(t => t.Rows.Count);

    /// <summary>
    /// The issues with the conflict modes chosen (missing tables: <see cref="ConflictMode.Skip"/>): with
    /// <see cref="ConflictMode.Abort"/>, an existing row is an error.
    /// </summary>
    public IReadOnlyList<CopyIssue> IssuesWith(IReadOnlyDictionary<TableRef, ConflictMode> modes) =>
    [
        .. Issues.Where(i => i.Severity == CopySeverity.Error),
        .. Tables.Where(t => t.Existing.Count > 0 && ModeOf(t, modes) == ConflictMode.Abort)
            .Select(t => new CopyIssue(CopySeverity.Error, t.Table, TextFormat.Plural(t.Existing.Count, RelationText.CopyAbortExistingOne, RelationText.CopyAbortExistingOther, t.Table.Name))),
        .. Issues.Where(i => i.Severity != CopySeverity.Error),
    ];

    /// <summary>Whether the copy can run with these modes and writes anything.</summary>
    public bool CanCopy(IReadOnlyDictionary<TableRef, ConflictMode> modes) =>
        IssuesWith(modes).All(i => i.Severity != CopySeverity.Error)
        && Tables.Any(t => t.NewRows > 0 || (t.Existing.Count > 0 && ModeOf(t, modes) == ConflictMode.Update));

    /// <summary>The chosen mode; skip if none is chosen or updating is not possible for the table.</summary>
    public static ConflictMode ModeOf(TableCopy table, IReadOnlyDictionary<TableRef, ConflictMode> modes) =>
        modes.GetValueOrDefault(table.Table) is var mode && (mode != ConflictMode.Update || table.CanUpdate) ? mode : ConflictMode.Skip;
}
