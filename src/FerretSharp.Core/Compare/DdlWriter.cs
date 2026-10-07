using FerretSharp.Core.Oracle;
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
            Add(Phase.Hints, target.Name, Commented($"DROP {ObjectKeyword(target.Kind)} {Qualified(target.Name)}"), target.Kind == TableKind.View
                ? "Löscht die View; Objekte, die sie verwenden, werden ungültig."
                : $"Löscht die {ObjectWord(target.Kind)} mit allen Daten.");
        }
        else if (reference.Kind != target.Kind)
        {
            Add(Phase.Hints, target.Name, $"-- {Qualified(target.Name)}: in der Referenz {ObjectWord(reference.Kind)}, im Ziel {ObjectWord(target.Kind)}",
                "Lässt sich nicht umwandeln: Objekt im Ziel löschen und nach der DDL der Referenz neu anlegen.");
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
            Add(Phase.Hints, table.Name, $"-- CREATE {ObjectKeyword(table.Kind)} {Qualified(table.Name)} AS …",
                $"Die Definition der {ObjectWord(table.Kind)} gehört nicht zum Vergleich: aus der DDL-Ansicht der Referenz übernehmen.");
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
            warnings.Add("Temporäre Tabelle: ON COMMIT ist nicht bekannt (Vorgabe DELETE ROWS) – mit der DDL der Referenz abgleichen.");
        }

        if (table.Partitioned)
        {
            warnings.Add("Die Partitionierung wird nicht übernommen – mit der DDL der Referenz abgleichen.");
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
            Add(Phase.Hints, name, $"-- ALTER {ObjectKeyword(target.Kind)} {Qualified(target.Name)} RENAME TO {Q(reference.Name)}", RenameWarning(ObjectWord(target.Kind)));
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
            Add(Phase.Hints, name, $"-- {Qualified(name)}: Organisation weicht ab (Referenz: {Organization(reference)}, Ziel: {Organization(target)})",
                "Lässt sich nicht per ALTER umstellen: Tabelle neu anlegen und Daten umkopieren.");
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
                warnings.Insert(0, "Schlägt fehl, wenn die Tabelle Zeilen hat (NOT NULL ohne Default).");
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
            Add(Phase.Hints, table, Commented($"ALTER TABLE {Qualified(table)} DROP COLUMN {Q(target.Name)}"), "Löscht die Spalte mit ihren Daten.");
            return;
        }

        var column = Q(target.Name);
        var alter = $"ALTER TABLE {Qualified(table)} MODIFY";
        var written = 0;
        if (!string.Equals(reference.Name, target.Name, StringComparison.Ordinal))
        {
            Add(Phase.Hints, table, $"-- ALTER TABLE {Qualified(table)} RENAME COLUMN {column} TO {Q(reference.Name)}", RenameWarning("Spalte"));
            written++;
        }

        if (reference.IsIdentity != target.IsIdentity || reference.IsVirtual != target.IsVirtual
            || (reference.IsVirtual && !string.Equals(reference.Default, target.Default, StringComparison.Ordinal)))
        {
            Add(Phase.Hints, table, $"-- {Qualified(table)}.{column}: Referenz {Describe(reference)}, Ziel {Describe(target)}",
                "Identity und virtuelle Spalten lassen sich nicht per MODIFY umstellen: neue Spalte anlegen und Daten umkopieren.");
            return;
        }

        var referenceType = TypeOf(reference);
        var targetType = TypeOf(target);
        if (!reference.IsVirtual && !string.Equals(referenceType, targetType, StringComparison.Ordinal))
        {
            written++;
            if (FamilyOf(reference.DataType) != FamilyOf(target.DataType) || IsLob(reference.DataType) || IsLob(target.DataType))
            {
                Add(Phase.Hints, table, $"-- {Qualified(table)}.{column}: Typ {targetType} → {referenceType}",
                    $"Der Wechsel von {targetType} zu {referenceType} geht nur über eine neue Spalte: anlegen, Daten umkopieren, alte löschen, umbenennen.");
            }
            else
            {
                Add(Phase.Columns, table, $"{alter} ({column} {referenceType})",
                    Widens(reference, target) ? null : "Kann an vorhandenen Daten scheitern (Länge, Genauigkeit oder BYTE/CHAR).");
            }
        }

        if (!reference.IsIdentity && !reference.IsVirtual
            && (reference.DefaultOnNull != target.DefaultOnNull || !string.Equals(reference.Default, target.Default, StringComparison.Ordinal)))
        {
            written++;
            var value = reference.Default ?? "NULL";
            Add(Phase.Columns, table, $"{alter} ({column} DEFAULT {(reference.DefaultOnNull ? "ON NULL " : "")}{value})",
                reference.DefaultOnNull && target.Nullable ? "DEFAULT ON NULL macht die Spalte NOT NULL: schlägt fehl, wenn sie NULL-Werte enthält." : Join(ExpressionWarnings(reference.Default)));
        }

        // DEFAULT ON NULL brings NOT NULL along (nothing to write). Taking ON NULL away takes NOT NULL with it (Oracle 23,
        // in the integration test); whether older versions keep it is not known, so the follow-up is written for both.
        if (!reference.IsIdentity && !reference.DefaultOnNull && target.DefaultOnNull)
        {
            written++;
            if (reference.Nullable)
            {
                // A follow-up of the DEFAULT step, so it stays next to it rather than among the hints at the end.
                Add(Phase.Columns, table, $"-- {alter} ({column} NULL)",
                    "Nur nötig, wenn die Spalte nach dem Entfernen von DEFAULT ON NULL noch NOT NULL ist (Oracle 23 hebt beides zusammen auf).");
            }
            else
            {
                Add(Phase.Columns, table, $"{alter} ({column} NOT NULL)",
                    "Schlägt fehl, wenn die Tabelle Zeilen mit NULL in dieser Spalte hat. ORA-01442: die Spalte ist schon NOT NULL – Schritt überspringen.");
            }
        }
        else if (!reference.IsIdentity && !reference.DefaultOnNull && reference.Nullable != target.Nullable)
        {
            written++;
            Add(Phase.Columns, table, reference.Nullable ? $"{alter} ({column} NULL)" : $"{alter} ({column} NOT NULL)",
                reference.Nullable ? null : "Schlägt fehl, wenn die Tabelle Zeilen mit NULL in dieser Spalte hat.");
        }

        if (!string.Equals(reference.Comment, target.Comment, StringComparison.Ordinal))
        {
            written++;
            Add(Phase.Columns, table, CommentOn(table, target.Name, reference.Comment));
        }

        // The comparison saw a difference this writer has no statement for; the order of columns has its own hint.
        if (written == 0 && !(options.ColumnOrder && reference.Position != target.Position))
        {
            Add(Phase.Hints, table, $"-- {Qualified(table)}.{column}: Referenz {referenceCell.Definition}, Ziel {targetCell.Definition}",
                "Für diesen Unterschied gibt es keinen Vorschlag – von Hand angleichen.");
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
            Add(Phase.Hints, target.Name, $"-- {Qualified(target.Name)}: Spaltenreihenfolge weicht ab ({string.Join(", ", referenceOrder)})",
                "Oracle kann Spalten nicht umsortieren; nur über eine neu angelegte Tabelle.");
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
            Add(Phase.Hints, table, $"-- ALTER TABLE {Qualified(table)} RENAME CONSTRAINT {Q(target.Name)} TO {Q(reference.Name)}", RenameWarning("Constraint"));
            return null;
        }

        var phase = PhaseOf(reference);
        Add(phase, table, drop, reference.Type is ConstraintType.PrimaryKey or ConstraintType.Unique
            ? "Wird gelöscht und neu angelegt. Verweisen Fremdschlüssel auf den Schlüssel, scheitert das Löschen (ORA-02273)."
            : "Wird gelöscht und neu angelegt; das Anlegen prüft die vorhandenen Zeilen.");
        AddConstraint(referenceTable, table, reference, existingTable: false);
        return reference;
    }

    private void AddConstraint(ObjectSnapshot referenceTable, string table, ConstraintInfo constraint, bool existingTable)
    {
        var warnings = new List<string>();
        if (existingTable && constraint.Enabled && constraint.Validated)
        {
            warnings.Add("Prüft die vorhandenen Zeilen und scheitert bei Verstößen.");
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
            Add(Phase.Hints, table, Commented(drop), "Löscht den Index. Stützt er einen Schlüssel, lehnt Oracle das ab (ORA-02429).");
            return;
        }

        if (!IsGeneratedName(reference.Name) && !IsGeneratedName(target.Name) && !string.Equals(reference.Name, target.Name, StringComparison.Ordinal)
            && string.Equals(referenceCell.Definition, targetCell.Definition, StringComparison.Ordinal))
        {
            Add(Phase.Hints, table, $"-- ALTER INDEX {Qualified(target.Name)} RENAME TO {Q(reference.Name)}", RenameWarning("Index"));
            return;
        }

        if (CreateIndex(reference, IndexName(referenceTable, reference), targetOwner, table) is not { } create)
        {
            Add(Phase.Hints, table, $"-- {Qualified(table)}: Index {Q(reference.Name)} vom Typ {reference.IndexType}",
                "Diesen Index-Typ legt der Vorschlag nicht an: aus der DDL der Referenz übernehmen.");
            return;
        }

        Add(Phase.Indexes, table, drop, "Wird gelöscht und neu aufgebaut. Stützt er einen Schlüssel, scheitert das Löschen (ORA-02429).");
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
                $"Die Spalten sind im Ziel schon indiziert ({twin.Name}); einen zweiten Index darauf lehnt Oracle ab (ORA-01408).");
            return;
        }

        if (CreateIndex(index, IndexName(referenceTable, index), targetOwner, table) is not { } create)
        {
            Add(Phase.Hints, table, $"-- {Qualified(table)}: Index {Q(index.Name)} vom Typ {index.IndexType}",
                "Diesen Index-Typ legt der Vorschlag nicht an: aus der DDL der Referenz übernehmen.");
            return;
        }

        Add(Phase.Indexes, table, create, Join(IndexWarnings(index)));
    }

    /// <summary>
    /// Indexes need a name. An index whose name Oracle generated in the reference (<c>SYS_C…</c>, e.g. left behind by a
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
            yield return $"Name frei gewählt; in der Referenz hat Oracle ihn vergeben ({index.Name}).";
        }

        if (index.Partitioned)
        {
            yield return "Die Partitionierung des Index wird nicht übernommen.";
        }
    }

    private void ViewHint(ObjectSnapshot target, IEnumerable<string> names) =>
        Add(Phase.Hints, target.Name, $"-- CREATE OR REPLACE {ObjectKeyword(target.Kind)} {Qualified(target.Name)} AS …  (abweichend: {string.Join(", ", names)})",
            $"Die Definition der {ObjectWord(target.Kind)} gehört nicht zum Vergleich: aus der DDL-Ansicht der Referenz übernehmen.");

    private IEnumerable<string> ColumnWarnings(ColumnInfo column)
    {
        if (column.IsIdentity)
        {
            yield return $"Identity {column.Name}: ALWAYS oder BY DEFAULT und die Optionen (Start, Schrittweite) sind nicht bekannt – mit der DDL der Referenz abgleichen.";
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
            yield return $"Der Ausdruck nennt das Schema der Referenz ({referenceOwner}) – im Ziel anpassen.";
        }
    }

    private static string DropConstraintWarning(ConstraintInfo constraint) => constraint.Type switch
    {
        ConstraintType.PrimaryKey or ConstraintType.Unique => "Löscht den Schlüssel. Verweisen Fremdschlüssel darauf, lehnt Oracle das ab (ORA-02273).",
        ConstraintType.ForeignKey => "Löscht den Fremdschlüssel: Oracle prüft die Beziehung dann nicht mehr.",
        _ => "Löscht die Prüfung.",
    };

    private static string RenameWarning(string what) =>
        $"{what} nur in anderer Schreibweise. Code und EF Core (quotet Namen) verweisen auf den genauen Namen – erst klären, welcher richtig ist.";

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

    private static bool IsGeneratedName(string name) => name.StartsWith("SYS_C", StringComparison.Ordinal);

    private static bool Widens(ColumnInfo reference, ColumnInfo target) =>
        reference.DataType == target.DataType && reference.CharSemantics == target.CharSemantics
        && (reference.Length ?? 0) >= (target.Length ?? 0)
        && reference.Scale == target.Scale
        && (reference.Precision is null || target.Precision is not null && reference.Precision >= target.Precision);

    private static string Describe(ColumnInfo column) =>
        column.IsIdentity ? "Identity" : column.IsVirtual ? $"virtuell AS ({column.Default})" : "normale Spalte";

    private static string Organization(ObjectSnapshot table)
    {
        var parts = new List<string>();
        if (table.IsIndexOrganized)
        {
            parts.Add("index-organisiert");
        }

        if (table.Temporary)
        {
            parts.Add("temporär");
        }

        if (table.Partitioned)
        {
            parts.Add("partitioniert");
        }

        return parts.Count == 0 ? "normal" : string.Join(", ", parts);
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
