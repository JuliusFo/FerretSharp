using System.Text;
using FerretSharp.Core.Ddl;
using FerretSharp.Core.Resources;
using FerretSharp.Core.Schema;

namespace FerretSharp.Core.Design;

// Constraints of the column designer (WP-23, stage 2): dropping existing ones, adding primary and unique keys, checks and
// foreign keys – a foreign key with its index unless one covers it (IndexAdvice).
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

    private sealed partial class Writer
    {
        private bool PrimaryKeyDropped => context.Constraints.Any(c => c.Type == ConstraintType.PrimaryKey && _dropped.Contains(c.Name));

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
            var taken = context.Constraints.Where(c => !_dropped.Contains(c.Name)).Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
            var keys = context.Constraints.Where(c => c.Type is ConstraintType.PrimaryKey or ConstraintType.Unique && !_dropped.Contains(c.Name)).ToList();
            var indexNames = context.Indexes.Select(i => i.Name).ToList();
            var checkedRows = false;

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
                Keep(OracleDdl.AddKind(added.Type), OracleDdl.AddConstraint(Table, OracleDdl.ConstraintClause(info, table => table)));
                if (added.Type is ConstraintType.PrimaryKey or ConstraintType.Unique)
                {
                    keys.Add(info);
                }

                // Checked rows: the existing ones must fit the new constraint – a table without rows has nothing to break.
                if (!checkedRows && facts.Of(new DataQuestion(DataQuestionKind.Rows)) is not 0)
                {
                    Warnings.Add(DesignText.ConstraintChecksRows);
                    checkedRows = true;
                }

                if (added.Type == ConstraintType.ForeignKey && !context.Indexes.Any(i => IndexAdvice.Covers(i, added.Columns))
                    && !keys.Any(k => k.Columns.Count >= added.Columns.Count && SameColumns(k.Columns.Take(added.Columns.Count).ToList(), added.Columns)))
                {
                    if (added.WithIndex)
                    {
                        var name = IndexDesign.SuggestName(current.Table.Name, added.Columns, indexNames);
                        indexNames.Add(name);
                        Keep(DdlStepKind.CreateIndex, IndexDesign.Ddl(new IndexDraft(name, added.Columns), current.Table.Ref));
                    }
                    else
                    {
                        Warnings.Add(TextFormat.Format(DesignText.ForeignKeyWithoutIndex, Label(added)));
                    }
                }
            }
        }

        private bool CheckConstraint(ConstraintDraft added, IReadOnlyList<ColumnInfo> columns, HashSet<string> taken, IReadOnlyList<ConstraintInfo> keys)
        {
            var label = Label(added);
            var problems = Problems.Count;
            if (added.Name.Contains('"', StringComparison.Ordinal))
            {
                Problems.Add(TextFormat.Format(DesignText.ColumnNameInvalid, added.Name));
            }
            else if (Encoding.UTF8.GetByteCount(added.Name) > IndexDesign.MaxNameBytes)
            {
                Problems.Add(TextFormat.Format(DesignText.ColumnNameTooLong, added.Name, IndexDesign.MaxNameBytes));
            }
            else if (taken.Contains(added.Name))
            {
                Problems.Add(TextFormat.Format(DesignText.ConstraintNameTaken, added.Name)); // ORA-02264
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
            constraint.Name.Length > 0 ? constraint.Name : $"{constraint.Type switch
            {
                ConstraintType.PrimaryKey => "PRIMARY KEY",
                ConstraintType.Unique => "UNIQUE",
                ConstraintType.ForeignKey => "FOREIGN KEY",
                _ => "CHECK",
            }} ({string.Join(", ", constraint.Columns)})";

        private static bool SameColumns(IReadOnlyList<string> a, IReadOnlyList<string> b) =>
            a.Count == b.Count && a.ToHashSet(StringComparer.Ordinal).SetEquals(b);
    }
}
