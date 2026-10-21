using FerretSharp.Core.Resources;
using FerretSharp.Core.Schema;

namespace FerretSharp.Core.Ddl;

/// <summary>Whether a writer knows what the user meant by a difference (<see cref="ColumnChange.Steps"/>).</summary>
public enum ChangeIntent
{
    /// <summary>
    /// The user said so (designer, WP-23; templates, WP-26): a column that is gone was dropped, one with another name was
    /// renamed – real DDL.
    /// </summary>
    Known,

    /// <summary>
    /// Only two states are known (schema comparison, WP-20): a column missing on one side may be dropped or renamed, so
    /// both stay a commented hint, like changes <c>MODIFY</c> cannot make.
    /// </summary>
    Unknown,
}

/// <summary>
/// The DDL that turns one column into another (R4b): one statement per change – rename, type, default, NULL, comment –, a
/// new column, or a dropped one. What a type change needs comes from <see cref="OracleTypes.ChangeOf"/> (the WP-23
/// experiment); the step's risk says it, its warning words it for a reader of the script. Shared by the schema comparison
/// and the designer, which adds its own checks against counted data.
/// </summary>
public static class ColumnChange
{
    /// <param name="table">The table the statements change.</param>
    /// <param name="before">The column as it is; null for a new one.</param>
    /// <param name="after">The column as it should be; null to drop it. Defaults and comments are compared as given.</param>
    /// <returns>The steps for this column, in the order written; <see cref="DdlScript.Order"/> puts them into a script.</returns>
    public static IReadOnlyList<DdlStep> Steps(TableRef table, ColumnInfo? before, ColumnInfo? after, ChangeIntent intent)
    {
        ArgumentNullException.ThrowIfNull(table);
        var steps = new List<DdlStep>();
        if (before is null)
        {
            if (after is not null)
            {
                Add(steps, table, after);
            }

            return steps;
        }

        if (after is null)
        {
            var drop = OracleDdl.DropColumn(table, before.Name);
            steps.Add(intent == ChangeIntent.Known
                ? Step(table, DdlStepKind.DropColumn, drop, DdlRisk.DataLoss, CompareText.DropColumn)
                : DdlStep.Hint(table.Name, drop, CompareText.DropColumn));
            return steps;
        }

        // Unknown: the rename is only a hint, so every statement after it names the column as it is.
        var column = intent == ChangeIntent.Known ? after.Name : before.Name;
        if (!string.Equals(before.Name, after.Name, StringComparison.Ordinal))
        {
            var rename = OracleDdl.RenameColumn(table, before.Name, after.Name);
            steps.Add(intent == ChangeIntent.Known
                ? Step(table, DdlStepKind.RenameColumn, rename)
                : DdlStep.Hint(table.Name, rename, TextFormat.Format(CompareText.RenameWarning, CompareText.RenameSubjectColumn)));
        }

        if (before.IsIdentity != after.IsIdentity || before.IsVirtual != after.IsVirtual
            || (after.IsVirtual && !string.Equals(after.Default, before.Default, StringComparison.Ordinal)))
        {
            // Not a MODIFY: a new column and copying. The comparison stops at the hint; the designer refuses the change
            // itself and keeps rename and comment.
            if (intent == ChangeIntent.Unknown)
            {
                steps.Add(DdlStep.Hint(table.Name, TextFormat.Format(CompareText.ColumnDiffersHint, Label(table, column), Describe(after), Describe(before)),
                    CompareText.IdentityOrVirtualWarning));
                return steps;
            }
        }
        else
        {
            ChangeType(steps, table, before, after, column, intent);
            ChangeDefaultAndNull(steps, table, before, after, column, intent);
        }

        if (!string.Equals(before.Comment, after.Comment, StringComparison.Ordinal))
        {
            steps.Add(Step(table, DdlStepKind.Comment, OracleDdl.CommentOnColumn(table, column, after.Comment)));
        }

        return steps;
    }

    /// <summary>
    /// Renames of columns of one table in an order that works (R4a): one whose new name another column still has waits for
    /// that one – A → B after B → C. Swapped names (A ↔ B) take a free name in between (<c>FS_RENAME_n</c>). In column
    /// order, Oracle refused the second (ORA-00957) after the drops before it had been committed.
    /// </summary>
    /// <param name="names">Names the table has or gets (a free name in between must be none of them).</param>
    public static IReadOnlyList<DdlStep> Renames(TableRef table, IReadOnlyList<(string From, string To)> renames, IEnumerable<string> names)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(renames);
        var steps = new List<DdlStep>();
        var pending = renames.ToList();
        var taken = names.Concat(renames.Select(r => r.To)).ToHashSet(StringComparer.Ordinal);
        while (pending.Count > 0)
        {
            var index = pending.FindIndex(r => !pending.Any(other => other.From == r.To));
            if (index < 0)
            {
                // Only cycles left: the first one steps aside.
                var aside = Enumerable.Range(1, int.MaxValue).Select(n => $"FS_RENAME_{n}").First(taken.Add);
                steps.Add(Step(table, DdlStepKind.RenameColumn, OracleDdl.RenameColumn(table, pending[0].From, aside)));
                pending[0] = (aside, pending[0].To);
                continue;
            }

            steps.Add(Step(table, DdlStepKind.RenameColumn, OracleDdl.RenameColumn(table, pending[index].From, pending[index].To)));
            pending.RemoveAt(index);
        }

        return steps;
    }

    /// <summary>A new NOT NULL column without a value for the existing rows (ORA-01758 if the table has any).</summary>
    public static bool NeedsEmptyTable(ColumnInfo column)
    {
        ArgumentNullException.ThrowIfNull(column);
        return !column.Nullable && column.Default is null && !column.IsIdentity && !column.IsVirtual && !column.DefaultOnNull;
    }

    private static void Add(List<DdlStep> steps, TableRef table, ColumnInfo column)
    {
        steps.Add(NeedsEmptyTable(column)
            ? Step(table, DdlStepKind.AddColumn, OracleDdl.AddColumn(table, column), DdlRisk.MayFailOnValues, CompareText.AddNotNullColumnFails)
            : Step(table, DdlStepKind.AddColumn, OracleDdl.AddColumn(table, column)));
        if (column.Comment is not null)
        {
            steps.Add(Step(table, DdlStepKind.Comment, OracleDdl.CommentOnColumn(table, column.Name, column.Comment)));
        }
    }

    /// <summary>
    /// A type change as <see cref="OracleTypes.ChangeOf"/> sees it (R4b, A4: the comparison called NUMBER(10) → NUMBER(10,2)
    /// and NVARCHAR2 → VARCHAR2 "may fail on data", while both need an empty column). A change between type families or to
    /// or from a LOB is a hint for the comparison: that is a new column and copying, not a MODIFY.
    /// </summary>
    private static void ChangeType(List<DdlStep> steps, TableRef table, ColumnInfo before, ColumnInfo after, string column, ChangeIntent intent)
    {
        if (after.IsVirtual)
        {
            return; // its type follows its expression
        }

        var change = OracleTypes.ChangeOf(before, after);
        if (change == ColumnTypeChange.Same)
        {
            return;
        }

        var (from, to) = (OracleTypes.DdlType(before), OracleTypes.DdlType(after));
        if (intent == ChangeIntent.Unknown && (change == ColumnTypeChange.NotPossible || OracleTypes.Family(before.DataType) != OracleTypes.Family(after.DataType)))
        {
            steps.Add(DdlStep.Hint(table.Name, TextFormat.Format(CompareText.TypeChangeHint, Label(table, column), from, to),
                TextFormat.Format(CompareText.TypeChangeWarning, from, to)));
            return;
        }

        var sql = OracleDdl.ModifyType(table, column, after);
        steps.Add(change switch
        {
            // What fits is padded all the same (review 3.44.0, A13: the padding was not mentioned then).
            ColumnTypeChange.MayFailOnValues => Step(table, DdlStepKind.ModifyType, sql, DdlRisk.MayFailOnValues,
                OracleTypes.ValueChangeOf(before, after) == ValueChange.PadsWithBlanks
                    ? CompareText.MayFailOnData + " " + CompareText.PadsWithBlanks
                    : CompareText.MayFailOnData),
            ColumnTypeChange.ChangesValues => Step(table, DdlStepKind.ModifyType, sql, DdlRisk.DataLoss, OracleTypes.ValueChangeOf(before, after) switch
            {
                ValueChange.CutsFractions => CompareText.CutsFractionalSeconds,
                ValueChange.KeepsBlanks => CompareText.KeepsBlanks, // review 3.44.0, A7
                _ => CompareText.PadsWithBlanks,
            }),
            ColumnTypeChange.NeedsEmptyColumn => Step(table, DdlStepKind.ModifyType, sql, DdlRisk.NeedsEmptyColumn, CompareText.NeedsEmptyColumn),
            ColumnTypeChange.NotPossible => Step(table, DdlStepKind.ModifyType, sql, DdlRisk.NotExecutable, TextFormat.Format(CompareText.TypeChangeWarning, from, to)),
            _ => Step(table, DdlStepKind.ModifyType, sql),
        });
    }

    /// <summary>
    /// Default and NULL: DEFAULT ON NULL brings NOT NULL along (nothing more to write); taking ON NULL away takes NOT NULL
    /// with it (Oracle 23, WP-20) – NOT NULL is written again if the column should keep it. Whether older versions keep
    /// it is not known, so the comparison adds a commented <c>NULL</c> for a column that should allow NULL.
    /// </summary>
    private static void ChangeDefaultAndNull(List<DdlStep> steps, TableRef table, ColumnInfo before, ColumnInfo after, string column, ChangeIntent intent)
    {
        if (after.IsIdentity)
        {
            return; // NOT NULL by itself, its "default" is its sequence
        }

        // A virtual column's default is its expression; it may still be NOT NULL.
        if (!after.IsVirtual && (before.DefaultOnNull != after.DefaultOnNull || !string.Equals(before.Default, after.Default, StringComparison.Ordinal)))
        {
            var sql = OracleDdl.ModifyDefault(table, column, after.Default, after.DefaultOnNull);
            steps.Add(after.DefaultOnNull && before.Nullable
                ? Step(table, DdlStepKind.ModifyDefault, sql, DdlRisk.MayFailOnValues, CompareText.DefaultOnNullMakesNotNull)
                : Step(table, DdlStepKind.ModifyDefault, sql));
        }

        if (after.DefaultOnNull)
        {
            return;
        }

        if (before.DefaultOnNull)
        {
            if (!after.Nullable)
            {
                steps.Add(Step(table, DdlStepKind.ModifyNull, OracleDdl.ModifyNull(table, column, nullable: false), DdlRisk.Safe, CompareText.NotNullAfterDefaultOnNull));
            }
            else if (intent == ChangeIntent.Unknown)
            {
                // A follow-up of the DEFAULT step: it stays next to it rather than among the hints at the end.
                steps.Add(Step(table, DdlStepKind.ModifyNull, OracleDdl.ModifyNull(table, column, nullable: true), DdlRisk.NotExecutable, CompareText.NullAfterDefaultOnNull));
            }
        }
        else if (before.Nullable != after.Nullable)
        {
            steps.Add(after.Nullable
                ? Step(table, DdlStepKind.ModifyNull, OracleDdl.ModifyNull(table, column, nullable: true))
                : Step(table, DdlStepKind.ModifyNull, OracleDdl.ModifyNull(table, column, nullable: false), DdlRisk.MayFailOnValues, CompareText.NotNullFails));
        }
    }

    private static DdlStep Step(TableRef table, DdlStepKind kind, string sql, DdlRisk risk = DdlRisk.Safe, string? warning = null) =>
        new(table.Name, kind, sql, risk, warning);

    /// <summary><c>"OWNER"."TABLE"."COLUMN"</c> in a hint.</summary>
    private static string Label(TableRef table, string column) =>
        $"{Oracle.OracleIdentifier.Qualify(table.Owner, table.Name)}.{OracleDdl.Q(column)}";

    private static string Describe(ColumnInfo column) =>
        column.IsIdentity ? CompareText.ColumnIdentity
        : column.IsVirtual ? TextFormat.Format(CompareText.ColumnVirtual, column.Default)
        : CompareText.ColumnNormal;
}
