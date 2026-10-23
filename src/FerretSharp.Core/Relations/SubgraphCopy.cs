using FerretSharp.Core.Data;
using FerretSharp.Core.Oracle;
using FerretSharp.Core.Query;
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
/// <param name="Convert">
/// Source values without the target's value type (NUMBER into VARCHAR2): converted as the grid takes a typed value – the
/// source's edit text parsed for the target column. Without it, ODP.NET would get values it cannot bind.
/// </param>
public sealed record ColumnMapping(int Source, int Target, bool Convert = false)
{
    /// <summary>The value written into the target column for <paramref name="row"/> (a row of <paramref name="source"/>).</summary>
    public object? ValueOf(TableDetails source, TableDetails target, RowData row) =>
        Convert ? Converted(source.Columns[Source], target.Columns[Target], row.Values[Source]).Value : row.Values[Source];

    /// <summary>The value as the target column takes it: unchanged if it has the column's value type already.</summary>
    internal static ParsedValue Converted(ColumnInfo source, ColumnInfo target, object? value) =>
        value is null || Fits(target, value) ? ParsedValue.Ok(value)
        : ColumnCategories.Of(target) == ColumnCategory.Clob ? ParsedValue.Ok(OracleTypeMapper.EditText(source, value))
        : OracleTypeMapper.Parse(target, OracleTypeMapper.EditText(source, value));

    /// <summary>The value has the type the session binds for the column (<see cref="OracleTypeMapper.BindType"/>).</summary>
    internal static bool Fits(ColumnInfo target, object value) => (ColumnCategories.Of(target), value) switch
    {
        (ColumnCategory.Text or ColumnCategory.Clob, string) => true,
        (ColumnCategory.Number, decimal or BigNumber) => true,
        (ColumnCategory.Date or ColumnCategory.Timestamp, DateTime) => true,
        (ColumnCategory.TimestampWithTimeZone, DateTime) => OracleTypeMapper.IsLocalTimeZone(target),
        (ColumnCategory.TimestampWithTimeZone, DateTimeOffset) => !OracleTypeMapper.IsLocalTimeZone(target),
        (ColumnCategory.Raw or ColumnCategory.Blob, byte[]) => true,
        _ => false,
    };
}

/// <summary>One table of a copy: its rows in the order to insert them, where they go and which exist there already.</summary>
public sealed class TableCopy
{
    internal TableCopy(
        TableRows source, TableDetails? target, IReadOnlyList<ColumnMapping> columns, IReadOnlyList<RowData> rows,
        IReadOnlySet<RowKey> existing, IReadOnlySet<RowKey> differing, IReadOnlyList<int>? conflictKey, IReadOnlyList<TableRef> triggers)
    {
        Source = source;
        Target = target;
        Columns = columns;
        Rows = rows;
        Existing = existing;
        Differing = differing;
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

    /// <summary>
    /// Of <see cref="Existing"/>, the rows whose target row holds other values in the copied columns (LOBs not compared):
    /// possibly another record under the same key.
    /// </summary>
    public IReadOnlySet<RowKey> Differing { get; }

    /// <summary>The target columns that recognize an existing row: the enforced primary key, else a unique key; null without.</summary>
    public IReadOnlyList<int>? ConflictKey { get; }

    /// <summary>Overwriting needs the target's primary key as the key (UPDATE … WHERE primary key).</summary>
    public bool CanUpdate => ConflictKey is not null && Target is { PrimaryKeyEnforced: true } target
                             && ConflictKey.Select(i => target.Columns[i].Name).Order(StringComparer.Ordinal)
                                 .SequenceEqual(target.PrimaryKey.Order(StringComparer.Ordinal), StringComparer.Ordinal);

    /// <summary>Enabled triggers on the target table, owner and name.</summary>
    public IReadOnlyList<TableRef> Triggers { get; }

    public int NewRows => Rows.Count - Existing.Count;

    /// <summary>The value a source row writes into the target column of <paramref name="column"/>.</summary>
    public object? ValueOf(RowData row, ColumnMapping column) => column.ValueOf(Source.Details, Target!, row);
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

    /// <summary>Rows per INSERT of a copy: array binding sends them in one round trip, the values stay in memory.</summary>
    public const int MaxBatchRows = 500;

    /// <summary>
    /// The writes of the copy with these modes, table after table in the plan's order: per table first the new rows in blocks
    /// of <see cref="MaxBatchRows"/>, then the updates of existing rows (conflict mode "update") – an existing row may get a
    /// reference to a new row of its own table (a new manager).
    /// </summary>
    /// <param name="source">The source connection's name for the description ("Copy from DEV: 57 rows").</param>
    /// <exception cref="InvalidOperationException">The copy cannot run with these modes (<see cref="CanCopy"/>).</exception>
    public CopyWrite ToWrite(IReadOnlyDictionary<TableRef, ConflictMode> modes, string source, int lockWaitSeconds)
    {
        if (!CanCopy(modes))
        {
            throw new InvalidOperationException("The copy has errors or nothing to write.");
        }

        var steps = new List<CopyStep>();
        foreach (var table in Tables)
        {
            var target = table.Target!;
            var columns = table.Columns.Select(c => c.Target).ToList();
            var fresh = table.Rows.Where(r => !table.Existing.Contains(r.Key))
                .Select(r => (IReadOnlyList<object?>)[.. table.Columns.Select(c => table.ValueOf(r, c))])
                .ToList();
            steps.AddRange(fresh.Chunk(MaxBatchRows).Select(rows => new CopyInsert(target, DmlBuilder.InsertBatch(target, columns, rows))));

            if (ModeOf(table, modes) == ConflictMode.Update)
            {
                var keyColumns = target.PrimaryKey.Select(target.IndexOf).ToHashSet();
                foreach (var row in table.Rows.Where(r => table.Existing.Contains(r.Key)))
                {
                    var key = new RowKey.PrimaryKey([.. target.PrimaryKey.Select(name => table.ValueOf(row, table.Columns.First(c => c.Target == target.IndexOf(name))))]);
                    var values = table.Columns.Where(c => !keyColumns.Contains(c.Target)).ToDictionary(c => c.Target, c => table.ValueOf(row, c));
                    if (values.Count > 0)
                    {
                        steps.Add(new CopyUpdate(target, key, values));
                    }
                }
            }
        }

        var write = new CopyWrite("", steps, lockWaitSeconds);
        return write with { Description = TextFormat.Plural(write.Rows, RelationText.CopyActionOne, RelationText.CopyActionOther, source) };
    }

    /// <summary>The chosen mode; skip if none is chosen or updating is not possible for the table.</summary>
    public static ConflictMode ModeOf(TableCopy table, IReadOnlyDictionary<TableRef, ConflictMode> modes) =>
        modes.GetValueOrDefault(table.Table) is var mode && (mode != ConflictMode.Update || table.CanUpdate) ? mode : ConflictMode.Skip;
}
