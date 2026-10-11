using FerretSharp.Core.Ddl;
using FerretSharp.Core.Oracle;
using FerretSharp.Core.Resources;
using FerretSharp.Core.Schema;
using static FerretSharp.Core.Ddl.OracleDdl;

namespace FerretSharp.Core.Compare;

/// <summary>
/// Collects the steps of one <see cref="SchemaDdl.Align"/> call: per object row the statements that make the target
/// side look like the reference side; <see cref="DdlScript.Order"/> sorts them at the end. The SQL is text for the user
/// (WP-20), never run here. Column changes come from <see cref="ColumnChange"/> with an unknown intent (R4b).
/// </summary>
internal sealed class DdlWriter(int referenceSide, int targetSide, string referenceOwner, string targetOwner, CompareOptions options)
{
    private readonly List<DdlStep> _steps = [];

    public IReadOnlyList<DdlStep> Steps() => DdlScript.Order(_steps);

    public void Object(CompareRow row)
    {
        var referenceCell = row.Cells[referenceSide];
        var targetCell = row.Cells[targetSide];
        var reference = Present(referenceCell) ? referenceCell.Object : null;
        var target = Present(targetCell) ? targetCell.Object : null;
        if (reference is null && target is null)
        {
            return;
        }

        if (target is null)
        {
            CreateObject(reference!);
        }
        else if (reference is null)
        {
            Hint(target.Name, DropObject(target.Kind, Target(target.Name)), target.Kind switch
            {
                TableKind.View => CompareText.DropView,
                TableKind.MaterializedView => CompareText.DropMaterializedView,
                _ => CompareText.DropTable,
            });
        }
        else if (reference.Kind != target.Kind)
        {
            Hint(target.Name, TextFormat.Format(CompareText.KindDiffersHint, Qualified(target.Name), ObjectWord(reference.Kind), ObjectWord(target.Kind)),
                CompareText.KindDiffersWarning);
        }
        else
        {
            AlignObject(row, reference, target);
        }
    }

    private void CreateObject(ObjectSnapshot table)
    {
        if (table.Kind != TableKind.Table)
        {
            Hint(table.Name, $"CREATE {ObjectKeyword(table.Kind)} {Qualified(table.Name)} AS …", DefinitionNotCompared(table.Kind));
            return;
        }

        var warnings = new List<string>();
        var entries = table.Columns.Select(ColumnDefinition).ToList();
        foreach (var column in table.Columns)
        {
            warnings.AddRange(ColumnWarnings(column));
        }

        // An index-organized table needs its primary key in the CREATE TABLE itself.
        var primaryKey = table.Constraints.FirstOrDefault(c => c.Type == ConstraintType.PrimaryKey);
        if (table.IsIndexOrganized && primaryKey is not null)
        {
            entries.Add(ConstraintClause(primaryKey, MapTable));
        }

        if (table.Temporary)
        {
            warnings.Add(CompareText.TemporaryTableOnCommit);
        }

        if (table.Partitioned)
        {
            warnings.Add(CompareText.PartitioningNotCopied);
        }

        Add(table.Name, DdlStepKind.CreateTable, CreateTable(Target(table.Name), entries, table.Temporary, table.IsIndexOrganized), Join(warnings));

        foreach (var column in table.Columns.Where(c => c.Comment is not null))
        {
            Add(table.Name, DdlStepKind.Comment, CommentOnColumn(Target(table.Name), column.Name, column.Comment));
        }

        foreach (var constraint in table.Constraints.Where(IsWritable))
        {
            if (!(table.IsIndexOrganized && constraint == primaryKey))
            {
                AddConstraint(table, table.Name, constraint, existingTable: false);
            }
        }

        foreach (var index in table.Indexes)
        {
            AddIndex(table, table.Name, index, created: _ => true, existing: []);
        }
    }

    private void AlignObject(CompareRow row, ObjectSnapshot reference, ObjectSnapshot target)
    {
        var name = target.Name;
        if (!string.Equals(reference.Name, target.Name, StringComparison.Ordinal))
        {
            Hint(name, RenameObject(target.Kind, Target(target.Name), reference.Name), RenameWarning(RenameSubject(target.Kind)));
        }

        var children = row.Children.Select(c => (Row: c, Reference: c.Cells[referenceSide], Target: c.Cells[targetSide])).ToList();
        var differing = children.Where(c => Differs(c.Row, c.Reference, c.Target)).ToList();

        if (reference.Kind == TableKind.View)
        {
            if (differing.Count > 0)
            {
                ViewHint(target, differing.Select(c => c.Row.Name));
            }

            return;
        }

        if (reference.IsIndexOrganized != target.IsIndexOrganized || reference.Temporary != target.Temporary || reference.Partitioned != target.Partitioned)
        {
            Hint(name, TextFormat.Format(CompareText.OrganizationDiffersHint, Qualified(name), Organization(reference), Organization(target)),
                CompareText.OrganizationWarning);
        }

        var columns = differing.Where(c => c.Row.Kind == CompareKind.Column).ToList();
        if (reference.Kind == TableKind.MaterializedView)
        {
            if (columns.Count > 0)
            {
                ViewHint(target, columns.Select(c => c.Row.Name));
            }
        }
        else
        {
            foreach (var (_, referenceColumn, targetColumn) in columns)
            {
                AlignColumn(name, referenceColumn, targetColumn);
            }

            ColumnOrderHint(reference, target);
        }

        // Keys added in this proposal bring their index along; indexes are written with that in mind.
        var created = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (_, referenceConstraint, targetConstraint) in differing.Where(c => c.Row.Kind is CompareKind.PrimaryKey or CompareKind.Unique or CompareKind.ForeignKey or CompareKind.Check))
        {
            var added = AlignConstraint(reference, name, referenceConstraint, targetConstraint);
            if (added is not null)
            {
                created.Add(added.Name);
            }
        }

        foreach (var (_, referenceIndex, targetIndex) in differing.Where(c => c.Row.Kind == CompareKind.Index))
        {
            AlignIndex(reference, target, name, referenceIndex, targetIndex, created);
        }
    }

    /// <summary>
    /// The column steps without knowing the intent: a missing column is added, an extra one, another name or a change
    /// <c>MODIFY</c> cannot make is a hint. The comparison adds what only it knows: identity options, schema names in
    /// expressions.
    /// </summary>
    private void AlignColumn(string table, CompareCell referenceCell, CompareCell targetCell)
    {
        var reference = Present(referenceCell) ? referenceCell.Column : null;
        var target = Present(targetCell) ? targetCell.Column : null;
        if (reference is null && target is null)
        {
            return;
        }

        var steps = ColumnChange.Steps(Target(table), target, reference, ChangeIntent.Unknown);
        foreach (var step in steps)
        {
            _steps.Add(step.Kind switch
            {
                DdlStepKind.AddColumn => step with { Warning = Join([.. Lines(step.Warning), .. ColumnWarnings(reference!)]) },
                DdlStepKind.ModifyDefault when step.Warning is null => step with { Warning = Join(ExpressionWarnings(reference!.Default)) },
                _ => step,
            });
        }

        // The comparison saw a difference this writer has no statement for; the order of columns has its own hint.
        if (steps.Count == 0 && reference is not null && target is not null && !(options.ColumnOrder && reference.Position != target.Position))
        {
            Hint(table, TextFormat.Format(CompareText.ColumnDiffersHint, $"{Qualified(table)}.{Q(target.Name)}", referenceCell.Definition, targetCell.Definition),
                CompareText.NoProposal);
        }
    }

    private void ColumnOrderHint(ObjectSnapshot reference, ObjectSnapshot target)
    {
        if (!options.ColumnOrder)
        {
            return;
        }

        var common = reference.Columns.Select(c => c.Name).Intersect(target.Columns.Select(c => c.Name), StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
        var referenceOrder = reference.Columns.Where(c => common.Contains(c.Name)).Select(c => c.Name);
        var targetOrder = target.Columns.Where(c => common.Contains(c.Name)).Select(c => c.Name);
        if (!referenceOrder.SequenceEqual(targetOrder, StringComparer.Ordinal))
        {
            Hint(target.Name, TextFormat.Format(CompareText.ColumnOrderHint, Qualified(target.Name), string.Join(", ", referenceOrder)),
                CompareText.ColumnOrderWarning);
        }
    }

    /// <returns>The reference constraint if this step adds it.</returns>
    private ConstraintInfo? AlignConstraint(ObjectSnapshot referenceTable, string table, CompareCell referenceCell, CompareCell targetCell)
    {
        var reference = Present(referenceCell) ? referenceCell.Constraint : null;
        var target = Present(targetCell) ? targetCell.Constraint : null;
        if (reference is not null && !IsWritable(reference) || target is not null && !IsWritable(target))
        {
            return null;
        }

        if (target is null)
        {
            if (reference is not null)
            {
                AddConstraint(referenceTable, table, reference, existingTable: true);
            }

            return reference;
        }

        var drop = DropConstraint(Target(table), target.Name);
        if (reference is null)
        {
            Hint(table, drop, DropConstraintWarning(target));
            return null;
        }

        if (!reference.GeneratedName && !target.GeneratedName && !string.Equals(reference.Name, target.Name, StringComparison.Ordinal)
            && string.Equals(referenceCell.Definition, targetCell.Definition, StringComparison.Ordinal))
        {
            Hint(table, RenameConstraint(Target(table), target.Name, reference.Name), RenameWarning(CompareText.RenameSubjectConstraint));
            return null;
        }

        Add(table, DdlStepKind.DropConstraint, drop, reference.Type is ConstraintType.PrimaryKey or ConstraintType.Unique
            ? CompareText.RecreateKey
            : CompareText.RecreateConstraint);
        AddConstraint(referenceTable, table, reference, existingTable: false);
        return reference;
    }

    private void AddConstraint(ObjectSnapshot referenceTable, string table, ConstraintInfo constraint, bool existingTable)
    {
        var warnings = new List<string>();
        if (existingTable && constraint.Enabled && constraint.Validated)
        {
            warnings.Add(CompareText.ChecksExistingRows);
        }

        warnings.AddRange(ExpressionWarnings(constraint.Condition));

        // A key whose index has a name of its own in the reference gets that index right away.
        string? usingIndex = null;
        if (BackingIndex(referenceTable, constraint) is { } index && !string.Equals(index.Name, constraint.Name, StringComparison.Ordinal) && !IsGeneratedName(index.Name))
        {
            usingIndex = CreateIndex(index, index.Name, targetOwner, table);
        }

        Add(table, AddKind(constraint.Type), OracleDdl.AddConstraint(Target(table), ConstraintClause(constraint, MapTable, usingIndex)), Join(warnings),
            existingTable && constraint.Enabled && constraint.Validated ? DdlRisk.MayFailOnValues : DdlRisk.Safe);
    }

    private void AlignIndex(ObjectSnapshot referenceTable, ObjectSnapshot targetTable, string table, CompareCell referenceCell, CompareCell targetCell, HashSet<string> created)
    {
        var reference = Present(referenceCell) ? referenceCell.Index : null;
        var target = Present(targetCell) ? targetCell.Index : null;
        if (target is null)
        {
            if (reference is not null)
            {
                AddIndex(referenceTable, table, reference, c => created.Contains(c.Name), targetTable.Indexes);
            }

            return;
        }

        var drop = DropIndex(targetOwner, target.Name);
        if (reference is null)
        {
            Hint(table, drop, CompareText.DropIndex);
            return;
        }

        if (!IsGeneratedName(reference.Name) && !IsGeneratedName(target.Name) && !string.Equals(reference.Name, target.Name, StringComparison.Ordinal)
            && string.Equals(referenceCell.Definition, targetCell.Definition, StringComparison.Ordinal))
        {
            Hint(table, RenameIndex(targetOwner, target.Name, reference.Name), RenameWarning(CompareText.RenameSubjectIndex));
            return;
        }

        if (CreateIndex(reference, IndexName(referenceTable, reference), targetOwner, table) is not { } create)
        {
            Hint(table, TextFormat.Format(CompareText.IndexTypeHint, Qualified(table), Q(reference.Name), reference.IndexType), CompareText.IndexTypeNotCreated);
            return;
        }

        Add(table, DdlStepKind.DropIndex, drop, CompareText.RebuildIndex);
        Add(table, DdlStepKind.CreateIndex, create, Join(IndexWarnings(reference)));
    }

    private void AddIndex(ObjectSnapshot referenceTable, string table, IndexInfo index, Func<ConstraintInfo, bool> created, IReadOnlyList<IndexInfo> existing)
    {
        if (index.IndexType == "IOT - TOP")
        {
            return; // the primary key of an index-organized table
        }

        // An index Oracle creates for a key added in this proposal (or that the key brings along via USING INDEX).
        var backs = referenceTable.Constraints.FirstOrDefault(c => c.Type is ConstraintType.PrimaryKey or ConstraintType.Unique && created(c)
            && string.Equals(BackingIndex(referenceTable, c)?.Name, index.Name, StringComparison.Ordinal)); // payloads need not be the snapshot's instances
        if (backs is not null)
        {
            return;
        }

        if (existing.FirstOrDefault(e => SameColumns(e, index)) is { } twin)
        {
            Hint(table, RenameIndex(targetOwner, twin.Name, index.Name), TextFormat.Format(CompareText.ColumnsAlreadyIndexed, twin.Name));
            return;
        }

        if (CreateIndex(index, IndexName(referenceTable, index), targetOwner, table) is not { } create)
        {
            Hint(table, TextFormat.Format(CompareText.IndexTypeHint, Qualified(table), Q(index.Name), index.IndexType), CompareText.IndexTypeNotCreated);
            return;
        }

        Add(table, DdlStepKind.CreateIndex, create, Join(IndexWarnings(index)));
    }

    /// <summary>
    /// Indexes need a name. An index whose name Oracle generated in the reference (<c>SYS_…</c>, e.g. left behind by a
    /// dropped key) gets a readable one instead of copying the generated name, which could clash with a later one.
    /// </summary>
    private static string IndexName(ObjectSnapshot table, IndexInfo index)
    {
        if (!IsGeneratedName(index.Name))
        {
            return index.Name;
        }

        var columns = index.Columns.Select(c => c.IsExpression ? "EXPR" : c.Name);
        var name = $"IX_{table.Name}_{string.Join("_", columns)}";
        return name.Length > 128 ? name[..128] : name;
    }

    private static IEnumerable<string> IndexWarnings(IndexInfo index)
    {
        if (IsGeneratedName(index.Name))
        {
            yield return TextFormat.Format(CompareText.IndexNameChosen, index.Name);
        }

        if (index.Partitioned)
        {
            yield return CompareText.IndexPartitioningNotCopied;
        }
    }

    private void ViewHint(ObjectSnapshot target, IEnumerable<string> names) =>
        Hint(target.Name,
            $"CREATE OR REPLACE {ObjectKeyword(target.Kind)} {Qualified(target.Name)} AS …  {TextFormat.Format(CompareText.ViewDiffers, string.Join(", ", names))}",
            DefinitionNotCompared(target.Kind));

    private static string DefinitionNotCompared(TableKind kind) =>
        kind == TableKind.MaterializedView ? CompareText.MaterializedViewDefinitionNotCompared : CompareText.ViewDefinitionNotCompared;

    /// <summary>What a column copied from the reference may not bring along: identity options, schema names in its default.</summary>
    private IEnumerable<string> ColumnWarnings(ColumnInfo column)
    {
        if (column.IsIdentity)
        {
            yield return TextFormat.Format(CompareText.IdentityOptionsUnknown, column.Name);
        }
        else
        {
            foreach (var warning in ExpressionWarnings(column.Default))
            {
                yield return warning;
            }
        }
    }

    /// <summary>Defaults, check conditions and virtual columns are copied as text; a schema name in them stays the reference's.</summary>
    private IEnumerable<string> ExpressionWarnings(string? expression)
    {
        if (expression is not null && !string.Equals(referenceOwner, targetOwner, StringComparison.Ordinal)
            && expression.Contains(Q(referenceOwner) + ".", StringComparison.Ordinal))
        {
            yield return TextFormat.Format(CompareText.ExpressionNamesReferenceSchema, referenceOwner);
        }
    }

    private static string DropConstraintWarning(ConstraintInfo constraint) => constraint.Type switch
    {
        ConstraintType.PrimaryKey or ConstraintType.Unique => CompareText.DropKey,
        ConstraintType.ForeignKey => CompareText.DropForeignKey,
        _ => CompareText.DropCheck,
    };

    /// <param name="what">What is renamed, as the subject of the sentence (<see cref="CompareText.RenameSubjectColumn"/> …).</param>
    private static string RenameWarning(string what) => TextFormat.Format(CompareText.RenameWarning, what);

    /// <summary>The object kind inside a sentence of the DDL proposal ("table").</summary>
    private static string ObjectWord(TableKind kind) => kind switch
    {
        TableKind.View => CompareText.ObjectView,
        TableKind.MaterializedView => CompareText.ObjectMaterializedView,
        _ => CompareText.ObjectTable,
    };

    /// <summary>The object kind at the start of <see cref="CompareText.RenameWarning"/> ("Table").</summary>
    private static string RenameSubject(TableKind kind) => kind switch
    {
        TableKind.View => CompareText.RenameSubjectView,
        TableKind.MaterializedView => CompareText.RenameSubjectMaterializedView,
        _ => CompareText.RenameSubjectTable,
    };

    private static bool IsWritable(ConstraintInfo constraint) =>
        constraint.Type is ConstraintType.PrimaryKey or ConstraintType.Unique or ConstraintType.ForeignKey
        || constraint.Type == ConstraintType.Check && !constraint.IsColumnNotNull;

    /// <summary>
    /// The reference's index with exactly the key's columns (plain, ascending): the one Oracle uses for it. Unique, except
    /// for a deferrable key – Oracle cannot use a unique index for it (ORA-14196) and creates a non-unique one.
    /// </summary>
    private static IndexInfo? BackingIndex(ObjectSnapshot table, ConstraintInfo constraint) =>
        constraint.Type is ConstraintType.PrimaryKey or ConstraintType.Unique
            ? table.Indexes.FirstOrDefault(i => (i.Unique || constraint.Deferrable) && i.Columns.All(c => !c.IsExpression && !c.Descending)
                && i.Columns.Select(c => c.Name).SequenceEqual(constraint.Columns, StringComparer.Ordinal))
            : null;

    private static bool SameColumns(IndexInfo a, IndexInfo b) =>
        a.Columns.Count == b.Columns.Count && a.Columns.Zip(b.Columns).All(p => p.First == p.Second);

    private static bool IsGeneratedName(string name) => IndexInfo.IsGenerated(name);

    private static string Organization(ObjectSnapshot table)
    {
        var parts = new List<string>();
        if (table.IsIndexOrganized)
        {
            parts.Add(CompareText.OrganizationIndexOrganized);
        }

        if (table.Temporary)
        {
            parts.Add(CompareText.OrganizationTemporary);
        }

        if (table.Partitioned)
        {
            parts.Add(CompareText.OrganizationPartitioned);
        }

        return parts.Count == 0 ? CompareText.OrganizationNormal : string.Join(", ", parts);
    }

    /// <summary>Two sides differ in a row: one lacks it, the names differ (other case) or the compared definitions do.</summary>
    private static bool Differs(CompareRow row, CompareCell reference, CompareCell target)
    {
        if (Present(reference) != Present(target))
        {
            return true;
        }

        if (!Present(reference))
        {
            return false;
        }

        var generated = reference.Constraint?.GeneratedName == true || target.Constraint?.GeneratedName == true
            || reference.Index is { } ri && IsGeneratedName(ri.Name) || target.Index is { } ti && IsGeneratedName(ti.Name);
        return !generated && !string.Equals(reference.Name ?? row.Name, target.Name ?? row.Name, StringComparison.Ordinal)
            || !string.Equals(reference.Definition, target.Definition, StringComparison.Ordinal);
    }

    private static bool Present(CompareCell cell) => cell.State != CellState.Missing;

    private TableRef MapTable(TableRef table) =>
        string.Equals(table.Owner, referenceOwner, StringComparison.Ordinal) ? new TableRef(targetOwner, table.Name) : table;

    /// <summary>The object on the target side.</summary>
    private TableRef Target(string name) => new(targetOwner, name);

    private string Qualified(string name) => OracleIdentifier.Qualify(targetOwner, name);

    private static IEnumerable<string> Lines(string? warning) => warning is null ? [] : warning.Split('\n');

    private static string? Join(IEnumerable<string> warnings) => warnings.ToList() is { Count: > 0 } list ? string.Join("\n", list) : null;

    private void Add(string table, DdlStepKind kind, string sql, string? warning = null, DdlRisk risk = DdlRisk.Safe) =>
        _steps.Add(new DdlStep(table, kind, sql, risk, warning));

    private void Hint(string table, string text, string? warning) => _steps.Add(DdlStep.Hint(table, text, warning));
}
