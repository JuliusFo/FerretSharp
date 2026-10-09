using FerretSharp.Core.Oracle;
using FerretSharp.Core.Resources;
using FerretSharp.Core.Schema;
using static FerretSharp.Core.Compare.DdlText;

namespace FerretSharp.Core.Compare;

/// <summary>
/// Collects the steps of one <see cref="SchemaDdl.Align"/> call: per object row the statements that make the target
/// side look like the reference side, sorted into phases at the end. The SQL is text for the user (WP-20), never run here.
/// </summary>
internal sealed class DdlWriter(int referenceSide, int targetSide, string referenceOwner, string targetOwner, CompareOptions options)
{
    /// <summary>Order of the steps in the proposal; within a phase by object, then in the order they were written.</summary>
    private enum Phase
    {
        Tables,
        Columns,
        Keys,
        Checks,
        ForeignKeys,
        Indexes,
        Hints,
    }

    private readonly List<(Phase Phase, int Sequence, DdlStep Step)> _steps = [];

    public IReadOnlyList<DdlStep> Steps() =>
        _steps.OrderBy(s => s.Phase).ThenBy(s => s.Step.Object, StringComparer.Ordinal).ThenBy(s => s.Sequence).Select(s => s.Step).ToList();

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
            Add(Phase.Hints, target.Name, Commented($"DROP {ObjectKeyword(target.Kind)} {Qualified(target.Name)}"), target.Kind switch
            {
                TableKind.View => CompareText.DropView,
                TableKind.MaterializedView => CompareText.DropMaterializedView,
                _ => CompareText.DropTable,
            });
        }
        else if (reference.Kind != target.Kind)
        {
            Add(Phase.Hints, target.Name,
                "-- " + TextFormat.Format(CompareText.KindDiffersHint, Qualified(target.Name), ObjectWord(reference.Kind), ObjectWord(target.Kind)),
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
            Add(Phase.Hints, table.Name, $"-- CREATE {ObjectKeyword(table.Kind)} {Qualified(table.Name)} AS …", DefinitionNotCompared(table.Kind));
            return;
        }

        var warnings = new List<string>();
        var lines = table.Columns.Select(column => "    " + ColumnDefinition(column)).ToList();
        foreach (var column in table.Columns)
        {
            warnings.AddRange(ColumnWarnings(column));
        }

        // An index-organized table needs its primary key in the CREATE TABLE itself.
        var primaryKey = table.Constraints.FirstOrDefault(c => c.Type == ConstraintType.PrimaryKey);
        if (table.IsIndexOrganized && primaryKey is not null)
        {
            lines.Add("    " + ConstraintClause(primaryKey, MapTable));
        }

        var create = table.Temporary ? "CREATE GLOBAL TEMPORARY TABLE" : "CREATE TABLE";
        var sql = $"{create} {Qualified(table.Name)} (\n{string.Join(",\n", lines)})";
        if (table.IsIndexOrganized)
        {
            sql += " ORGANIZATION INDEX";
        }

        if (table.Temporary)
        {
            warnings.Add(CompareText.TemporaryTableOnCommit);
        }

        if (table.Partitioned)
        {
            warnings.Add(CompareText.PartitioningNotCopied);
        }

        Add(Phase.Tables, table.Name, sql, Join(warnings));

        foreach (var column in table.Columns.Where(c => c.Comment is not null))
        {
            Add(Phase.Columns, table.Name, CommentOn(table.Name, column.Name, column.Comment));
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
            Add(Phase.Hints, name, $"-- ALTER {ObjectKeyword(target.Kind)} {Qualified(target.Name)} RENAME TO {Q(reference.Name)}", RenameWarning(RenameSubject(target.Kind)));
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
            Add(Phase.Hints, name, "-- " + TextFormat.Format(CompareText.OrganizationDiffersHint, Qualified(name), Organization(reference), Organization(target)),
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

    private void AlignColumn(string table, CompareCell referenceCell, CompareCell targetCell)
    {
        var reference = Present(referenceCell) ? referenceCell.Column : null;
        var target = Present(targetCell) ? targetCell.Column : null;
        if (target is null)
        {
            if (reference is null)
            {
                return;
            }

            var warnings = ColumnWarnings(reference).ToList();
            if (!reference.Nullable && reference.Default is null && !reference.IsIdentity && !reference.IsVirtual)
            {
                warnings.Insert(0, CompareText.AddNotNullColumnFails);
            }

            Add(Phase.Columns, table, $"ALTER TABLE {Qualified(table)} ADD ({ColumnDefinition(reference)})", Join(warnings));
            if (reference.Comment is not null)
            {
                Add(Phase.Columns, table, CommentOn(table, reference.Name, reference.Comment));
            }

            return;
        }

        if (reference is null)
        {
            Add(Phase.Hints, table, Commented($"ALTER TABLE {Qualified(table)} DROP COLUMN {Q(target.Name)}"), CompareText.DropColumn);
            return;
        }

        var column = Q(target.Name);
        var alter = $"ALTER TABLE {Qualified(table)} MODIFY";
        var written = 0;
        if (!string.Equals(reference.Name, target.Name, StringComparison.Ordinal))
        {
            Add(Phase.Hints, table, $"-- ALTER TABLE {Qualified(table)} RENAME COLUMN {column} TO {Q(reference.Name)}", RenameWarning(CompareText.RenameSubjectColumn));
            written++;
        }

        if (reference.IsIdentity != target.IsIdentity || reference.IsVirtual != target.IsVirtual
            || (reference.IsVirtual && !string.Equals(reference.Default, target.Default, StringComparison.Ordinal)))
        {
            Add(Phase.Hints, table, "-- " + TextFormat.Format(CompareText.ColumnDiffersHint, $"{Qualified(table)}.{column}", Describe(reference), Describe(target)),
                CompareText.IdentityOrVirtualWarning);
            return;
        }

        var referenceType = OracleTypes.DdlType(reference);
        var targetType = OracleTypes.DdlType(target);
        if (!reference.IsVirtual && !string.Equals(referenceType, targetType, StringComparison.Ordinal))
        {
            written++;
            if (OracleTypes.Family(reference.DataType) != OracleTypes.Family(target.DataType)
                || OracleTypes.IsLobOrLong(reference.DataType) || OracleTypes.IsLobOrLong(target.DataType))
            {
                Add(Phase.Hints, table, "-- " + TextFormat.Format(CompareText.TypeChangeHint, $"{Qualified(table)}.{column}", targetType, referenceType),
                    TextFormat.Format(CompareText.TypeChangeWarning, targetType, referenceType));
            }
            else
            {
                Add(Phase.Columns, table, $"{alter} ({column} {referenceType})",
                    Widens(reference, target) ? null : CompareText.MayFailOnData);
            }
        }

        if (!reference.IsIdentity && !reference.IsVirtual
            && (reference.DefaultOnNull != target.DefaultOnNull || !string.Equals(reference.Default, target.Default, StringComparison.Ordinal)))
        {
            written++;
            var value = reference.Default ?? "NULL";
            Add(Phase.Columns, table, $"{alter} ({column} DEFAULT {(reference.DefaultOnNull ? "ON NULL " : "")}{value})",
                reference.DefaultOnNull && target.Nullable ? CompareText.DefaultOnNullMakesNotNull : Join(ExpressionWarnings(reference.Default)));
        }

        // DEFAULT ON NULL brings NOT NULL along (nothing to write). Taking ON NULL away takes NOT NULL with it (Oracle 23,
        // in the integration test); whether older versions keep it is not known, so the follow-up is written for both.
        if (!reference.IsIdentity && !reference.DefaultOnNull && target.DefaultOnNull)
        {
            written++;
            if (reference.Nullable)
            {
                // A follow-up of the DEFAULT step, so it stays next to it rather than among the hints at the end.
                Add(Phase.Columns, table, $"-- {alter} ({column} NULL)", CompareText.NullAfterDefaultOnNull);
            }
            else
            {
                Add(Phase.Columns, table, $"{alter} ({column} NOT NULL)", CompareText.NotNullAfterDefaultOnNull);
            }
        }
        else if (!reference.IsIdentity && !reference.DefaultOnNull && reference.Nullable != target.Nullable)
        {
            written++;
            Add(Phase.Columns, table, reference.Nullable ? $"{alter} ({column} NULL)" : $"{alter} ({column} NOT NULL)",
                reference.Nullable ? null : CompareText.NotNullFails);
        }

        if (!string.Equals(reference.Comment, target.Comment, StringComparison.Ordinal))
        {
            written++;
            Add(Phase.Columns, table, CommentOn(table, target.Name, reference.Comment));
        }

        // The comparison saw a difference this writer has no statement for; the order of columns has its own hint.
        if (written == 0 && !(options.ColumnOrder && reference.Position != target.Position))
        {
            Add(Phase.Hints, table,
                "-- " + TextFormat.Format(CompareText.ColumnDiffersHint, $"{Qualified(table)}.{column}", referenceCell.Definition, targetCell.Definition),
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
            Add(Phase.Hints, target.Name, "-- " + TextFormat.Format(CompareText.ColumnOrderHint, Qualified(target.Name), string.Join(", ", referenceOrder)),
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

        var drop = $"ALTER TABLE {Qualified(table)} DROP CONSTRAINT {Q(target.Name)}";
        if (reference is null)
        {
            Add(Phase.Hints, table, Commented(drop), DropConstraintWarning(target));
            return null;
        }

        if (!reference.GeneratedName && !target.GeneratedName && !string.Equals(reference.Name, target.Name, StringComparison.Ordinal)
            && string.Equals(referenceCell.Definition, targetCell.Definition, StringComparison.Ordinal))
        {
            Add(Phase.Hints, table, $"-- ALTER TABLE {Qualified(table)} RENAME CONSTRAINT {Q(target.Name)} TO {Q(reference.Name)}", RenameWarning(CompareText.RenameSubjectConstraint));
            return null;
        }

        var phase = PhaseOf(reference);
        Add(phase, table, drop, reference.Type is ConstraintType.PrimaryKey or ConstraintType.Unique
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

        Add(PhaseOf(constraint), table, $"ALTER TABLE {Qualified(table)} ADD {ConstraintClause(constraint, MapTable, usingIndex)}", Join(warnings));
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

        var drop = $"DROP INDEX {Qualified(target.Name)}";
        if (reference is null)
        {
            Add(Phase.Hints, table, Commented(drop), CompareText.DropIndex);
            return;
        }

        if (!IsGeneratedName(reference.Name) && !IsGeneratedName(target.Name) && !string.Equals(reference.Name, target.Name, StringComparison.Ordinal)
            && string.Equals(referenceCell.Definition, targetCell.Definition, StringComparison.Ordinal))
        {
            Add(Phase.Hints, table, $"-- ALTER INDEX {Qualified(target.Name)} RENAME TO {Q(reference.Name)}", RenameWarning(CompareText.RenameSubjectIndex));
            return;
        }

        if (CreateIndex(reference, IndexName(referenceTable, reference), targetOwner, table) is not { } create)
        {
            Add(Phase.Hints, table, "-- " + TextFormat.Format(CompareText.IndexTypeHint, Qualified(table), Q(reference.Name), reference.IndexType),
                CompareText.IndexTypeNotCreated);
            return;
        }

        Add(Phase.Indexes, table, drop, CompareText.RebuildIndex);
        Add(Phase.Indexes, table, create, Join(IndexWarnings(reference)));
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
            Add(Phase.Hints, table, $"-- ALTER INDEX {Qualified(twin.Name)} RENAME TO {Q(index.Name)}",
                TextFormat.Format(CompareText.ColumnsAlreadyIndexed, twin.Name));
            return;
        }

        if (CreateIndex(index, IndexName(referenceTable, index), targetOwner, table) is not { } create)
        {
            Add(Phase.Hints, table, "-- " + TextFormat.Format(CompareText.IndexTypeHint, Qualified(table), Q(index.Name), index.IndexType),
                CompareText.IndexTypeNotCreated);
            return;
        }

        Add(Phase.Indexes, table, create, Join(IndexWarnings(index)));
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
        Add(Phase.Hints, target.Name,
            $"-- CREATE OR REPLACE {ObjectKeyword(target.Kind)} {Qualified(target.Name)} AS …  {TextFormat.Format(CompareText.ViewDiffers, string.Join(", ", names))}",
            DefinitionNotCompared(target.Kind));

    private static string DefinitionNotCompared(TableKind kind) =>
        kind == TableKind.MaterializedView ? CompareText.MaterializedViewDefinitionNotCompared : CompareText.ViewDefinitionNotCompared;

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

    private static Phase PhaseOf(ConstraintInfo constraint) => constraint.Type switch
    {
        ConstraintType.PrimaryKey or ConstraintType.Unique => Phase.Keys,
        ConstraintType.Check => Phase.Checks,
        _ => Phase.ForeignKeys,
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

    private static bool Widens(ColumnInfo reference, ColumnInfo target) =>
        reference.DataType == target.DataType && reference.CharSemantics == target.CharSemantics
        && (reference.Length ?? 0) >= (target.Length ?? 0)
        && reference.Scale == target.Scale
        && (reference.Precision is null || target.Precision is not null && reference.Precision >= target.Precision);

    private static string Describe(ColumnInfo column) =>
        column.IsIdentity ? CompareText.ColumnIdentity
        : column.IsVirtual ? TextFormat.Format(CompareText.ColumnVirtual, column.Default)
        : CompareText.ColumnNormal;

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

    private string Qualified(string name) => OracleIdentifier.Qualify(targetOwner, name);

    private string CommentOn(string table, string column, string? comment) =>
        $"COMMENT ON COLUMN {Qualified(table)}.{Q(column)} IS {Literal(comment)}";

    private static string? Join(IEnumerable<string> warnings) => warnings.ToList() is { Count: > 0 } list ? string.Join("\n", list) : null;

    private void Add(Phase phase, string table, string sql, string? warning = null) =>
        _steps.Add((phase, _steps.Count, new DdlStep(table, sql, warning)));
}
