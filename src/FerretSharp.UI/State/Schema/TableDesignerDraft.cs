using System.Globalization;
using FerretSharp.Core.Audit;
using FerretSharp.Core.Design;
using FerretSharp.Core.Oracle;
using FerretSharp.Core.Resources;
using FerretSharp.Core.Schema;
using FerretSharp.UI.Resources;

namespace FerretSharp.UI.State;

/// <summary>The edit mode of a table tab: its draft, and how the last run failed (shown again after the tab was rebuilt).</summary>
public sealed record DesignerSession(TableDesignerDraft Draft, DdlRunResult? Failure = null);

/// <summary>
/// The edit mode of the "Columns" view (WP-23, stage 2): the table's columns and constraints as editable rows, and the
/// <see cref="TableDraft"/> they make. Plain state – the view renders it, <see cref="TableDesign"/> writes the DDL.
/// </summary>
public sealed class TableDesignerDraft
{
    public TableDesignerDraft(TableDetails table, IReadOnlyList<ConstraintInfo> constraints)
    {
        ArgumentNullException.ThrowIfNull(table);
        Table = table;
        TableName = table.Table.Name;
        Columns = table.Columns.Select(DesignColumn.Of).ToList();
        Existing = constraints.Where(c => !c.IsColumnNotNull).Select(c => new ExistingConstraint(c)).ToList();
    }

    /// <summary>The tab shown; kept across a <see cref="Rebase"/>.</summary>
    public DesignerSection Section { get; set; }

    public TableDetails Table { get; }

    /// <summary>The table's name for suggested constraint names; a new table's changes as it is typed (WP-23, stage 3).</summary>
    public string TableName { get; set; } = "";

    public List<DesignColumn> Columns { get; }

    /// <summary>The table's constraints, to rename, change or drop (NOT NULL checks belong to their columns).</summary>
    public IReadOnlyList<ExistingConstraint> Existing { get; }

    /// <summary>Names of existing constraints to drop (their names in the table).</summary>
    public HashSet<string> DroppedConstraints { get; } = new(StringComparer.Ordinal);

    public List<DesignConstraint> AddedConstraints { get; } = [];

    /// <summary>
    /// The tablespace of the indexes this draft creates (WP-67: new keys', new foreign keys'), dictionary form; empty for the
    /// owner's default. Kept across a <see cref="Rebase"/>.
    /// </summary>
    public string IndexTablespace { get; set; } = "";

    /// <summary>What there is to choose and suggest (WP-67), read once the designer opens; kept across a <see cref="Rebase"/>.</summary>
    public Tablespaces Tablespaces { get; set; } = Tablespaces.None;

    public bool HasChanges => ColumnsChanged || ConstraintsChanged;

    public bool ColumnsChanged => Columns.Any(c => c.IsChanged);

    public bool ConstraintsChanged => DroppedConstraints.Count > 0 || AddedConstraints.Count > 0 || Existing.Any(c => c.IsChanged);

    /// <summary>
    /// Audit table and trigger go into the plan (WP-26): created, or brought up to date with the draft. Null until the view
    /// knows whether the table has an audit table (<see cref="AuditAtStart"/>).
    /// </summary>
    public bool? WithAudit { get; set; }

    /// <summary>
    /// Whether the audit objects go into the plan: the box ticked and able to take part (<see cref="AuditDesignRules.CanAudit"/>)
    /// – a box ticked before creating was switched off, kept over a rebase, creates nothing.
    /// </summary>
    public bool AuditTakesPart(bool? hasAuditTable, bool createAllowed) =>
        WithAudit == true && AuditDesignRules.CanAudit(hasAuditTable, createAllowed);

    /// <summary>The names of the columns as drafted (renamed, new), without dropped ones – for constraint columns.</summary>
    public IReadOnlyList<string> ColumnNames => Columns.Where(c => !c.Dropped && c.Name.Trim().Length > 0).Select(c => c.Name.Trim()).ToList();

    /// <summary>A new column at the end (Oracle adds columns there), with the text semantics most columns of the table use.</summary>
    public DesignColumn AddColumn()
    {
        var text = Table.Columns.Where(c => OracleTypes.HasLengthSemantics(c.DataType)).ToList();
        var column = DesignColumn.New(charSemantics: text.Count > 0 && text.Count(c => c.CharSemantics) * 2 >= text.Count);
        Columns.Add(column);
        return column;
    }

    /// <summary>A new column goes away; an existing one is marked dropped (and comes back on the second call).</summary>
    public void ToggleDrop(DesignColumn column)
    {
        if (column.IsNew)
        {
            Columns.Remove(column);
        }
        else
        {
            column.Dropped = !column.Dropped;
        }
    }

    public DesignConstraint AddConstraint(ConstraintType type)
    {
        var constraint = new DesignConstraint { Type = type };
        AddedConstraints.Add(constraint);
        SuggestName(constraint);
        return constraint;
    }

    /// <summary>The suggested name for its type and columns, unless the user typed one.</summary>
    public void SuggestName(DesignConstraint constraint)
    {
        ArgumentNullException.ThrowIfNull(constraint);
        var taken = Existing.Select(c => c.Before.Name).Concat(Existing.Select(c => OracleIdentifier.Normalize(c.Name)))
            .Concat(AddedConstraints.Where(c => c != constraint).Select(c => c.Name));
        constraint.Suggest(TableDesign.SuggestConstraintName(TableName, constraint.Type, constraint.Columns, constraint.References?.Name, taken));
    }

    /// <summary>An existing constraint is dropped – or, the second time, kept again.</summary>
    public void ToggleDropConstraint(string name)
    {
        if (!DroppedConstraints.Remove(name))
        {
            DroppedConstraints.Add(name);
        }
    }

    /// <summary>Another kind of constraint: the name follows, unless typed.</summary>
    public void SetType(DesignConstraint constraint, ConstraintType type)
    {
        ArgumentNullException.ThrowIfNull(constraint);
        constraint.Type = type;
        SuggestName(constraint);
    }

    /// <summary>
    /// A column for the constraint, at the end. A foreign key's referenced column follows the key of the referenced table,
    /// position by position (once its structure is known, <see cref="Referenced"/>).
    /// </summary>
    public void AddColumn(DesignConstraint constraint, string? column)
    {
        ArgumentNullException.ThrowIfNull(constraint);
        if (string.IsNullOrEmpty(column) || constraint.Columns.Contains(column))
        {
            return;
        }

        constraint.Columns.Add(column);
        if (constraint.References is { } references && Referenced.TryGetValue(references, out var target))
        {
            constraint.ReferencedColumns.Add(KeyColumn(target, constraint.Columns.Count - 1));
        }

        SuggestName(constraint);
    }

    /// <summary>Takes the column at <paramref name="at"/> out of the constraint, with its referenced column.</summary>
    public void RemoveColumn(DesignConstraint constraint, int at)
    {
        ArgumentNullException.ThrowIfNull(constraint);
        constraint.Columns.RemoveAt(at);
        if (at < constraint.ReferencedColumns.Count)
        {
            constraint.ReferencedColumns.RemoveAt(at);
        }

        SuggestName(constraint);
    }

    /// <summary>
    /// The foreign key references <paramref name="table"/> (null: none chosen). Its referenced columns are the table's
    /// primary key, position by position, as soon as the table's structure is known – the view loads it if
    /// <see cref="Referenced"/> lacks it, then calls <see cref="Learn"/>.
    /// </summary>
    public void Reference(DesignConstraint constraint, TableRef? table)
    {
        ArgumentNullException.ThrowIfNull(constraint);
        constraint.ReferencedColumns.Clear();
        constraint.References = table;
        SuggestName(constraint);
        FillReferenced(constraint);
    }

    /// <summary>
    /// The structures of the tables new foreign keys reference, as loaded (their keys and column types); kept across a
    /// <see cref="Rebase"/>.
    /// </summary>
    public Dictionary<TableRef, TableDetails> Referenced { get; } = [];

    /// <summary>A referenced table's structure arrived: foreign keys on it without referenced columns get its key.</summary>
    public void Learn(TableDetails table)
    {
        ArgumentNullException.ThrowIfNull(table);
        Referenced[table.Table.Ref] = table;
        foreach (var constraint in AddedConstraints.Where(c => c.References == table.Table.Ref))
        {
            FillReferenced(constraint);
        }
    }

    /// <summary>The columns another table offers to reference; this table's as drafted; null while not loaded.</summary>
    public IReadOnlyList<string>? ColumnsOf(TableRef table) =>
        table == Table.Table.Ref ? ColumnNames : Referenced.TryGetValue(table, out var details) ? details.Columns.Select(c => c.Name).ToList() : null;

    private void FillReferenced(DesignConstraint constraint)
    {
        if (constraint is { References: { } references, ReferencedColumns.Count: 0 } && Referenced.TryGetValue(references, out var target))
        {
            constraint.ReferencedColumns.AddRange(constraint.Columns.Select((_, i) => KeyColumn(target, i)));
        }
    }

    /// <summary>The primary key's column at a position; the first column where the key has none.</summary>
    private static string KeyColumn(TableDetails table, int at) => table.PrimaryKey.ElementAtOrDefault(at) ?? table.Columns[0].Name;

    /// <summary>
    /// The same draft laid over the table as it is now – after DDL that stopped halfway (WP-23): the draft says how the table
    /// should be, so what already ran drops out by itself. A column is found by the name it should have (renamed, added),
    /// otherwise by its old name (not renamed yet); a dropped column that is gone and a constraint already added or dropped
    /// are done. An existing constraint is found the same way; one replaced halfway goes on from where the run stopped
    /// (<see cref="TakeOverReplacements"/>).
    /// </summary>
    public TableDesignerDraft Rebase(TableDetails table, IReadOnlyList<ConstraintInfo> constraints)
    {
        var next = new TableDesignerDraft(table, constraints);
        var added = new List<DesignColumn>();
        foreach (var row in Columns)
        {
            var target = next.Columns.FirstOrDefault(c => c.Before!.Name == row.Name.Trim() && !row.Dropped)
                         ?? (row.Before is { } before ? next.Columns.FirstOrDefault(c => c.Before!.Name == before.Name) : null);
            if (target is not null)
            {
                target.CopyFrom(row);
            }
            else if (!row.Dropped)
            {
                added.Add(row.AsNew());
            }
        }

        next.Columns.AddRange(added);
        foreach (var row in Existing)
        {
            var target = next.Existing.FirstOrDefault(c => c.Before.Name == OracleIdentifier.Normalize(row.Name))
                         ?? next.Existing.FirstOrDefault(c => c.Before.Name == row.Before.Name);
            target?.CopyFrom(row);
        }

        TakeOverReplacements(next);
        var existing = next.Existing.Select(c => c.Before.Name).ToHashSet(StringComparer.Ordinal);
        next.DroppedConstraints.UnionWith(DroppedConstraints.Where(existing.Contains));
        next.AddedConstraints.AddRange(AddedConstraints.Where(c => c.Name.Length == 0 || !existing.Contains(OracleIdentifier.Normalize(c.Name))));
        foreach (var (reference, details) in Referenced)
        {
            next.Referenced[reference] = details;
        }

        (next.WithAudit, next.Section, next.IndexTablespace, next.Tablespaces) = (WithAudit, Section, IndexTablespace, Tablespaces);
        return next;
    }

    /// <summary>
    /// Changed constraints whose replacement stopped halfway (a lock, a lost session), so the rest of it is still to do:
    /// <list type="bullet">
    /// <item>A check whose new one is in already (a check the table did not have, with the new condition – under
    /// <see cref="TableDesign.ReplacementPrefix"/>, its new name or one Oracle gave): the old one, if still there, is dropped
    /// and the new one gets the name it should have.</item>
    /// <item>A foreign key that was dropped but not added again: it is added as a new constraint, with its new rule, on the
    /// columns' drafted names; its index is there already.</item>
    /// </list>
    /// </summary>
    private void TakeOverReplacements(TableDesignerDraft next)
    {
        var had = Existing.Select(c => c.Before.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var row in Existing.Where(r => !DroppedConstraints.Contains(r.Before.Name) && TableDesign.Rechecks(r.Before, r.ToEdit())))
        {
            var old = next.Existing.FirstOrDefault(c => c.Before.Name == row.Before.Name);
            if (row.Before.Type == ConstraintType.Check)
            {
                if (next.Existing.FirstOrDefault(c => c.Before.Type == ConstraintType.Check && !had.Contains(c.Before.Name)
                        && (c.Before.Condition ?? "").Trim() == row.Condition.Trim()) is not { } replacement)
                {
                    continue;
                }

                // An Oracle-generated name stays the one Oracle gave the new check, unless the user typed one.
                replacement.Name = row.Before.GeneratedName && !row.IsRenamed ? replacement.Before.Name : OracleIdentifier.Normalize(row.Name);
                if (old is not null)
                {
                    old.CopyFrom(new ExistingConstraint(old.Before));
                    next.DroppedConstraints.Add(old.Before.Name);
                }
            }
            else if (old is null && next.Existing.All(c => c.Before.Name != OracleIdentifier.Normalize(row.Name)))
            {
                string Drafted(string column) => Columns.FirstOrDefault(c => c.Before?.Name == column)?.Name.Trim() ?? column;
                var foreignKey = new DesignConstraint
                {
                    Type = ConstraintType.ForeignKey,
                    Name = row.Name,
                    References = row.Before.References,
                    DeleteRule = row.DeleteRule,
                    WithIndex = false,
                };
                foreignKey.Columns.AddRange(row.Before.Columns.Select(Drafted));
                foreignKey.ReferencedColumns.AddRange(row.Before.References == Table.Table.Ref ? row.Before.ReferencedColumns.Select(Drafted) : row.Before.ReferencedColumns);
                next.AddedConstraints.Add(foreignKey);
            }
        }
    }

    /// <summary>The draft, and what is wrong with the fields before the designer can check it (numbers that are none).</summary>
    public (TableDraft Draft, IReadOnlyList<string> Problems) ToDraft()
    {
        var problems = new List<string>();
        var columns = new List<ColumnDraft>();
        foreach (var row in Columns)
        {
            var (column, problem) = row.ToColumn();
            if (problem is not null)
            {
                problems.Add(problem);
            }

            // A column whose numbers are none stays as it is, so the rest can still be checked.
            columns.Add(new ColumnDraft(row.Before?.Name, column ?? row.Before ?? new ColumnInfo(row.Name, row.Type, null, false, null, null, true, false, null, 0), row.Dropped));
        }

        var draft = new TableDraft(columns)
        {
            DroppedConstraints = [.. DroppedConstraints],
            AddedConstraints = AddedConstraints.Select(c => c.ToDraft()).ToList(),
            ChangedConstraints = Existing.Where(c => c.IsChanged && !DroppedConstraints.Contains(c.Before.Name)).Select(c => c.ToEdit()).ToList(),
            IndexTablespace = IndexTablespace.Length > 0 ? IndexTablespace : null,
        };
        return (draft, problems);
    }
}
