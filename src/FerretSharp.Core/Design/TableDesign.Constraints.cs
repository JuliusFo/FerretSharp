using FerretSharp.Core.Oracle;
using System.Text;
using FerretSharp.Core.Ddl;
using FerretSharp.Core.Resources;
using FerretSharp.Core.Schema;

namespace FerretSharp.Core.Design;

// Constraints of the column designer (WP-23, stage 2): dropping existing ones, adding primary and unique keys, checks and
// foreign keys – a foreign key with its index unless one covers it (IndexAdvice); renaming existing ones (a key with its
// index of the same name), a check's new condition and a foreign key's new delete rule (designer tabs).
public static partial class TableDesign
{
    /// <summary>
    /// A name for a new constraint: <c>PK_&lt;TABLE&gt;</c>, <c>UK_&lt;TABLE&gt;_&lt;COLUMNS&gt;</c>,
    /// <c>CK_&lt;TABLE&gt;_&lt;COLUMNS&gt;</c>, <c>FK_&lt;TABLE&gt;_&lt;REFERENCED&gt;</c> – a plain name, cut to 128 bytes, counted up
    /// if <paramref name="taken"/> has it.
    /// </summary>
    public static string SuggestConstraintName(string table, ConstraintType type, IReadOnlyList<string> columns, string? referenced, IEnumerable<string> taken)
    {
        var columnPart = string.Concat(columns.Select(c => "_" + c));
        var stem = type switch
        {
            ConstraintType.PrimaryKey => $"PK_{table}",
            ConstraintType.Unique => $"UK_{table}{columnPart}",
            ConstraintType.ForeignKey => $"FK_{table}_{referenced}",
            _ => $"CK_{table}{columnPart}",
        };
        return IndexDesign.PlainName(stem, taken);
    }

    /// <summary>
    /// The name a new check has in between while it replaces one that keeps its name (<c>FS_CHECK_1</c> …): renamed to the
    /// old name once the old one is gone. One left in the table means a run stopped halfway (the designer picks it up).
    /// </summary>
    public const string ReplacementPrefix = "FS_CHECK_";

    /// <summary>The edit changes what the constraint checks – a check's condition, a foreign key's delete rule – so the rows are checked again.</summary>
    public static bool Rechecks(ConstraintInfo before, ConstraintEdit edit)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(edit);
        return before.Type switch
        {
            ConstraintType.Check => edit.Condition?.Trim() is { } condition && condition != (before.Condition ?? "").Trim(),
            ConstraintType.ForeignKey => ConstraintInfo.RuleOf(edit.DeleteRule) != before.OnDelete,
            _ => false,
        };
    }

    private sealed partial class Writer
    {
        private bool PrimaryKeyDropped => context.Constraints.Any(c => c.Type == ConstraintType.PrimaryKey && _dropped.Contains(c.Name));

        /// <summary>The indexes this plan creates, in <see cref="TableDraft.IndexTablespace"/> (<see cref="DesignPlan.NewIndexes"/>).</summary>
        private readonly List<NewIndex> _newIndexes = [];

        /// <summary>
        /// <see cref="TableDraft.IndexTablespace"/> as the DDL takes it: none for a temporary table (its indexes go to the
        /// temporary tablespace) or for a name that is none.
        /// </summary>
        private string? _indexTablespace;

        /// <summary>New keys an existing index backs: said only while the index tablespace is offered (other indexes come).</summary>
        private readonly List<string> _keyNotes = [];

        /// <summary>A chosen tablespace that can go into the DDL; a name Oracle could not take is a problem instead (never quoted).</summary>
        private string? Usable(string? tablespace)
        {
            if (string.IsNullOrEmpty(tablespace))
            {
                return null;
            }

            if (OracleIdentifier.Problem(tablespace) is { } problem)
            {
                Problems.Add(problem);
                return null;
            }

            return tablespace;
        }

        /// <summary>Names existing constraints get by renaming: taken for new ones, like the names they have now.</summary>
        private readonly HashSet<string> _renamedTo = new(StringComparer.Ordinal);

        /// <summary>New and changed constraints check the rows – unless the table has none.</summary>
        private void WarnRowsChecked()
        {
            if (facts.Of(new DataQuestion(DataQuestionKind.Rows)) is not 0)
            {
                Warnings.Add(DesignText.ConstraintChecksRows);
            }
        }

        /// <summary>
        /// Renamed and changed constraints. A renamed one keeps its definition (RENAME CONSTRAINT, a key's index of its name
        /// with it); a check with a new condition is added before the old one goes, so the old one stays when the rows break
        /// the new one; a foreign key with a new delete rule is dropped and added again (Oracle refuses a second one on the
        /// same columns, ORA-02275). The edit of a dropped constraint is none.
        /// </summary>
        private void EditConstraints(TableDraft draft)
        {
            var edits = draft.ChangedConstraints.Where(e => !_dropped.Contains(e.Original)).ToList();
            var columnNames = draft.Columns.Where(c => c is { IsNew: false, Dropped: false })
                .ToDictionary(c => c.Original!, c => c.Column.Name, StringComparer.Ordinal);
            var droppedColumns = draft.Columns.Where(c => c is { IsNew: false, Dropped: true }).Select(c => c.Original!).ToHashSet(StringComparer.Ordinal);
            foreach (var edit in edits)
            {
                if (context.Constraints.FirstOrDefault(c => c.Name == edit.Original) is not { } before)
                {
                    continue;
                }

                var renamed = edit.Name != before.Name;
                if (renamed && !CheckRename(edit, edits))
                {
                    continue;
                }

                // Added again after the column went: Oracle dropped the constraint with it, the new one has no column (ORA-00904).
                var rechecks = Rechecks(before, edit);
                if (rechecks && before.Columns.FirstOrDefault(droppedColumns.Contains) is { } dropped)
                {
                    Problems.Add(TextFormat.Format(DesignText.ConstraintColumnDropped, before.Name, dropped));
                    continue;
                }

                // An Oracle-generated name stays generated unless the user gives one.
                var after = before with { Name = edit.Name, GeneratedName = before.GeneratedName && !renamed };
                if (rechecks && before.Type == ConstraintType.Check)
                {
                    ReplaceCheck(before, after with { Condition = edit.Condition!.Trim() });
                }
                else if (rechecks)
                {
                    // The columns by the names they have when it is added again, after the renames.
                    string Drafted(string column) => columnNames.GetValueOrDefault(column, column);
                    var self = before.References == current.Table.Ref;
                    Keep(DdlStepKind.DropConstraint, OracleDdl.DropConstraint(Table, before.Name));
                    Keep(DdlStepKind.AddForeignKey, OracleDdl.AddConstraint(Table, OracleDdl.ConstraintClause(after with
                    {
                        Columns = before.Columns.Select(Drafted).ToList(),
                        ReferencedColumns = self ? before.ReferencedColumns.Select(Drafted).ToList() : before.ReferencedColumns,
                        DeleteRule = ConstraintInfo.RuleOf(edit.DeleteRule),
                    }, table => table)));
                    Warnings.Add(TextFormat.Format(DesignText.ForeignKeyRecreated, before.Name));
                    WarnRowsChecked();
                }
                else if (renamed)
                {
                    Keep(DdlStepKind.RenameConstraint, OracleDdl.RenameConstraint(Table, before.Name, edit.Name));
                    RenameIndex(before, edit);
                }

                if (renamed)
                {
                    Warnings.Add(TextFormat.Format(DesignText.ConstraintRenamed, before.Name, edit.Name));
                }
            }
        }

        /// <summary>
        /// The new name: given, valid, not another constraint's – now or after its own rename (a swap would need a name in
        /// between).
        /// </summary>
        private bool CheckRename(ConstraintEdit edit, IReadOnlyList<ConstraintEdit> edits)
        {
            var problems = Problems.Count;
            if (edit.Name.Length == 0)
            {
                Problems.Add(TextFormat.Format(DesignText.ConstraintNameMissing, edit.Original));
            }
            else if (OracleIdentifier.Problem(edit.Name) is { } problem)
            {
                Problems.Add(problem);
            }
            else if (context.Constraints.Any(c => c.Name == edit.Name && !_dropped.Contains(c.Name))
                     || edits.Any(e => e != edit && e.Name == edit.Name))
            {
                Problems.Add(TextFormat.Format(DesignText.ConstraintNameTaken, edit.Name)); // ORA-02264
            }
            else if (context.Elsewhere.Constraints.Contains(edit.Name))
            {
                Problems.Add(TextFormat.Format(DesignText.ConstraintNameTakenInSchema, edit.Name)); // unique in the schema (review 3.54.0, A14)
            }
            else if (OracleIdentifier.NeedsQuotes(edit.Name))
            {
                Warnings.Add(TextFormat.Format(DesignText.NameNeedsQuotes, edit.Name));
            }

            if (Problems.Count > problems)
            {
                return false;
            }

            _renamedTo.Add(edit.Name);
            return true;
        }

        /// <summary>
        /// The new check first: it checks the rows, and the old one goes only once it is in. Keeping the old one's name, it is
        /// added under a free name in between (<c>FS_CHECK_n</c>) and renamed at the end; an Oracle-generated name is
        /// generated again.
        /// </summary>
        private void ReplaceCheck(ConstraintInfo before, ConstraintInfo after)
        {
            if (after.Condition!.Length == 0)
            {
                Problems.Add(TextFormat.Format(DesignText.CheckNeedsCondition, before.Name));
                return;
            }

            if (!CheckExpression(before.Name, after.Condition))
            {
                return;
            }

            var keepsName = !after.GeneratedName && after.Name == before.Name;
            var taken = context.Constraints.Select(c => c.Name).Concat(_renamedTo).Concat(context.Elsewhere.Constraints).ToHashSet(StringComparer.Ordinal);
            var added = keepsName ? after with { Name = Enumerable.Range(1, int.MaxValue).Select(n => $"{ReplacementPrefix}{n}").First(taken.Add) } : after;
            _renamedTo.Add(added.Name);
            Keep(DdlStepKind.AddCheck, OracleDdl.AddConstraint(Table, OracleDdl.ConstraintClause(added, table => table)));
            Keep(DdlStepKind.ReplaceConstraint, OracleDdl.DropConstraint(Table, before.Name));
            if (keepsName)
            {
                Keep(DdlStepKind.ReplaceConstraint, OracleDdl.RenameConstraint(Table, added.Name, before.Name));
            }

            Warnings.Add(TextFormat.Format(DesignText.CheckReplaced, before.Name));
            WarnRowsChecked();
        }

        /// <summary>A renamed primary or unique key's index of the same name – the usual case, also for <c>SYS_C…</c> – follows it.</summary>
        private void RenameIndex(ConstraintInfo before, ConstraintEdit edit)
        {
            if (!edit.RenameIndex || before.Type is not (ConstraintType.PrimaryKey or ConstraintType.Unique)
                || context.Indexes.FirstOrDefault(i => i.Name == before.Name) is not { } index)
            {
                return;
            }

            if (context.Indexes.Any(i => i.Name == edit.Name) || context.Elsewhere.Indexes.Contains(edit.Name))
            {
                Problems.Add(TextFormat.Format(context.Elsewhere.Indexes.Contains(edit.Name) ? DesignText.IndexNameTakenInSchema : DesignText.IndexNameTaken, edit.Name));
                return;
            }

            Keep(DdlStepKind.RenameIndex, OracleDdl.RenameIndex(index.Owner, index.Name, edit.Name));
        }

        private void DropConstraints(IReadOnlyList<string> names)
        {
            _dropped = names.ToHashSet(StringComparer.Ordinal);
            foreach (var constraint in context.Constraints.Where(c => _dropped.Contains(c.Name)))
            {
                if (constraint.Type is ConstraintType.PrimaryKey or ConstraintType.Unique)
                {
                    var referencing = context.Incoming.Where(fk => SameColumns(fk.ToColumns, constraint.Columns)).Select(fk => fk.Name).ToList();
                    if (referencing.Count > 0)
                    {
                        Problems.Add(TextFormat.Format(DesignText.DropReferencedKey, constraint.Name, string.Join(", ", referencing))); // ORA-02273
                    }
                }

                Keep(DdlStepKind.DropConstraint, OracleDdl.DropConstraint(Table, constraint.Name));
            }
        }

        private void AddConstraints(TableDraft draft, IReadOnlyList<ColumnDraft> kept)
        {
            var columns = kept.Select(c => c.Column).Where(c => !string.IsNullOrWhiteSpace(c.Name)).ToList();
            var taken = context.Constraints.Where(c => !_dropped.Contains(c.Name)).Select(c => c.Name).Concat(_renamedTo).ToHashSet(StringComparer.Ordinal);
            var keys = context.Constraints.Where(c => c.Type is ConstraintType.PrimaryKey or ConstraintType.Unique && !_dropped.Contains(c.Name)).ToList();
            var indexNames = context.Indexes.Select(i => i.Name).Concat(_renamedTo).Concat(context.Elsewhere.Indexes).ToList();
            _indexTablespace = context.Tablespaces.TemporaryTable ? null : Usable(draft.IndexTablespace);

            // Keys before foreign keys: a new foreign key may reference a new key of the same table.
            foreach (var added in draft.AddedConstraints.OrderBy(c => c.Type switch
                     {
                         ConstraintType.PrimaryKey => 0,
                         ConstraintType.Unique => 1,
                         ConstraintType.Check => 2,
                         _ => 3,
                     }))
            {
                if (!CheckConstraint(added, columns, taken, keys))
                {
                    continue;
                }

                if (added.Name.Length > 0)
                {
                    taken.Add(added.Name);
                }

                var info = new ConstraintInfo(added.Name, added.Type, added.Columns, added.Condition?.Trim(), added.References, added.ReferencedColumns ?? [],
                    added.DeleteRule, Enabled: true, Validated: true, Deferrable: false, InitiallyDeferred: false, GeneratedName: added.Name.Length == 0);
                string? indexTablespace = null;
                if (added.Type is ConstraintType.PrimaryKey or ConstraintType.Unique)
                {
                    indexTablespace = KeyIndexTablespace(added);
                }

                Keep(OracleDdl.AddKind(added.Type), OracleDdl.AddConstraint(Table, OracleDdl.ConstraintClause(info, table => table, indexTablespace: indexTablespace)));
                if (added.Type is ConstraintType.PrimaryKey or ConstraintType.Unique)
                {
                    keys.Add(info);
                }

                // Checked rows: the existing ones must fit the new constraint – a table without rows has nothing to break.
                WarnRowsChecked();

                if (added.Type == ConstraintType.ForeignKey && !context.Indexes.Any(i => IndexAdvice.Backs(i, added.Columns))
                    && !keys.Any(k => StartsWith(k.Columns, added.Columns)))
                {
                    if (added.WithIndex)
                    {
                        var name = IndexDesign.SuggestName(current.Table.Name, added.Columns, indexNames);
                        indexNames.Add(name);
                        Created(new NewIndex(name, Label(added)));
                        Keep(DdlStepKind.CreateIndex, IndexDesign.Ddl(new IndexDraft(name, added.Columns) { Tablespace = _indexTablespace }, current.Table.Ref));
                    }
                    else
                    {
                        Warnings.Add(TextFormat.Format(DesignText.ForeignKeyWithoutIndex, Label(added)));
                    }
                }
            }

            if (_newIndexes.Count > 0)
            {
                Warnings.AddRange(_keyNotes);
                IndexDesign.CheckTablespace(_indexTablespace, Table.Owner, context.Tablespaces, Problems, Warnings);
            }
        }

        /// <summary>An index the plan creates – where the index tablespace applies; a temporary table's go elsewhere.</summary>
        private void Created(NewIndex index)
        {
            if (!context.Tablespaces.TemporaryTable)
            {
                _newIndexes.Add(index);
            }
        }

        /// <summary>
        /// The tablespace for the index a new key creates (WP-67). None if an index of the table backs the key already
        /// (<see cref="IndexAdvice.BacksKey"/>): Oracle takes that one, and refuses <c>USING INDEX TABLESPACE</c> on its column
        /// list (ORA-01408, measured against Oracle 23); the user learns where the key's index is. The index of a key this plan
        /// drops counts too: one created before its key (scripts of SQL Developer, DBMS_METADATA) stays with
        /// <c>DROP CONSTRAINT</c>, and with the clause the new key would fail after the old one is gone (WP-67 review). Keys
        /// this plan adds before need no look: their indexes are unique, and a key on the same columns is refused anyway.
        /// </summary>
        private string? KeyIndexTablespace(ConstraintDraft added)
        {
            // An index named like a key this plan drops is that key's own: it stays only if it was created before the key
            // (Oracle does not say which; an implicit one goes with the key). Any other backing index stays for sure.
            bool OfDroppedKey(IndexInfo index) => _dropped.Contains(index.Name)
                && context.Constraints.Any(c => c.Name == index.Name && c.Type is ConstraintType.PrimaryKey or ConstraintType.Unique);
            var backing = context.Indexes.Where(i => IndexAdvice.BacksKey(i, added.Columns)).OrderBy(OfDroppedKey).FirstOrDefault();
            if (backing is null)
            {
                Created(new NewIndex(Label(added))); // Oracle names the key's index after it
                return _indexTablespace;
            }

            // Said also while no other index comes: the chosen tablespace may not apply (review of WP-67).
            if (_indexTablespace is not null && OfDroppedKey(backing))
            {
                Warnings.Add(TextFormat.Format(DesignText.KeyMayUseDroppedIndex, Label(added), backing.Name, backing.Name));
            }
            else if (_indexTablespace is not null)
            {
                _keyNotes.Add(TextFormat.Format(DesignText.KeyUsesIndex, Label(added), backing.Name));
            }

            return null;
        }

        /// <summary>The index of a key with <paramref name="key"/> starts with <paramref name="columns"/> (in any order).</summary>
        private static bool StartsWith(IReadOnlyList<string> key, IReadOnlyList<string> columns) =>
            key.Count >= columns.Count && SameColumns(key.Take(columns.Count).ToList(), columns);

        private bool CheckConstraint(ConstraintDraft added, IReadOnlyList<ColumnInfo> columns, HashSet<string> taken, IReadOnlyList<ConstraintInfo> keys)
        {
            var label = Label(added);
            var problems = Problems.Count;
            if (OracleIdentifier.Problem(added.Name) is { } problem)
            {
                Problems.Add(problem);
            }
            else if (taken.Contains(added.Name))
            {
                Problems.Add(TextFormat.Format(DesignText.ConstraintNameTaken, added.Name)); // ORA-02264
            }
            else if (context.Elsewhere.Constraints.Contains(added.Name))
            {
                Problems.Add(TextFormat.Format(DesignText.ConstraintNameTakenInSchema, added.Name)); // ORA-02264 (review 3.54.0, A14)
            }
            else if (added.Type is ConstraintType.PrimaryKey or ConstraintType.Unique && context.Elsewhere.Indexes.Contains(added.Name)
                     && !context.Indexes.Any(i => !_dropped.Contains(i.Name) && IndexAdvice.BacksKey(i, added.Columns)))
            {
                // The key creates an index of its name unless one of the table backs it (review of R8, WP-67) – here not the
                // index of a key this plan drops, though one created before its key stays (backlog: then no name is needed).
                Problems.Add(TextFormat.Format(DesignText.IndexNameTakenInSchema, added.Name)); // ORA-00955
            }

            if (added.Type != ConstraintType.Check && added.Columns.Count == 0)
            {
                Problems.Add(TextFormat.Format(DesignText.ConstraintNeedsColumns, label));
            }

            foreach (var name in added.Columns.Where(c => !columns.Any(column => column.Name == c)).Distinct())
            {
                Problems.Add(TextFormat.Format(DesignText.ConstraintColumnUnknown, label, name));
            }

            foreach (var twice in added.Columns.GroupBy(c => c, StringComparer.Ordinal).Where(g => g.Count() > 1))
            {
                Problems.Add(TextFormat.Format(DesignText.ConstraintColumnTwice, label, twice.Key));
            }

            switch (added.Type)
            {
                case ConstraintType.PrimaryKey when keys.Any(k => k.Type == ConstraintType.PrimaryKey):
                    Problems.Add(DesignText.PrimaryKeyExists); // ORA-02260
                    break;
                case ConstraintType.PrimaryKey or ConstraintType.Unique when keys.FirstOrDefault(k => SameColumns(k.Columns, added.Columns)) is { } twin:
                    Problems.Add(TextFormat.Format(DesignText.KeyExists, label, twin.Name)); // ORA-02261
                    break;
                case ConstraintType.Check when string.IsNullOrWhiteSpace(added.Condition):
                    Problems.Add(TextFormat.Format(DesignText.CheckNeedsCondition, label));
                    break;
                case ConstraintType.Check:
                    CheckExpression(label, added.Condition);
                    break;
                case ConstraintType.ForeignKey:
                    CheckForeignKey(added, label, columns, keys);
                    break;
            }

            return Problems.Count == problems;
        }

        private void CheckForeignKey(ConstraintDraft added, string label, IReadOnlyList<ColumnInfo> columns, IReadOnlyList<ConstraintInfo> keys)
        {
            if (added.References is not { } references)
            {
                Problems.Add(TextFormat.Format(DesignText.ForeignKeyNeedsTable, label));
                return;
            }

            var referencedColumns = added.ReferencedColumns ?? [];
            if (referencedColumns.Count != added.Columns.Count)
            {
                Problems.Add(TextFormat.Format(DesignText.ForeignKeyColumnCount, label, added.Columns.Count, referencedColumns.Count));
                return;
            }

            // The referenced table as it will be: this one with the draft's columns and keys, another as the schema has it.
            var self = references == current.Table.Ref;
            IReadOnlyList<ColumnInfo>? targetColumns;
            IReadOnlyList<IReadOnlyList<string>>? targetKeys;
            if (self)
            {
                (targetColumns, targetKeys) = (columns, keys.Select(k => k.Columns).ToList());
            }
            else if (context.Referenced.TryGetValue(references, out var target))
            {
                (targetColumns, targetKeys) = (target.Columns, [target.PrimaryKey, .. target.UniqueKeys]);
            }
            else
            {
                return; // not loaded: Oracle checks it
            }

            if (!targetKeys.Any(k => SameColumns(k, referencedColumns)))
            {
                Problems.Add(TextFormat.Format(DesignText.ForeignKeyNeedsKey, label, references.Name, string.Join(", ", referencedColumns))); // ORA-02270
                return;
            }

            for (var i = 0; i < added.Columns.Count; i++)
            {
                var from = columns.FirstOrDefault(c => c.Name == added.Columns[i]);
                var to = targetColumns.FirstOrDefault(c => c.Name == referencedColumns[i]);
                if (from is not null && to is not null && OracleTypes.Family(from.DataType) != OracleTypes.Family(to.DataType))
                {
                    Problems.Add(TextFormat.Format(DesignText.ForeignKeyTypes, label, from.Name, OracleTypes.DisplayType(from), to.Name, OracleTypes.DisplayType(to))); // ORA-02267
                }
            }
        }

        /// <summary>The constraint's name, or its kind and columns while it has none.</summary>
        private static string Label(ConstraintDraft constraint) =>
            constraint.Name.Length > 0 ? constraint.Name : $"{ConstraintTypes.Keyword(constraint.Type) ?? "CHECK"} ({string.Join(", ", constraint.Columns)})";

        private static bool SameColumns(IReadOnlyList<string> a, IReadOnlyList<string> b) =>
            a.Count == b.Count && a.ToHashSet(StringComparer.Ordinal).SetEquals(b);
    }
}
