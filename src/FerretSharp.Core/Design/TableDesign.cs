using System.Text;
using System.Text.RegularExpressions;
using FerretSharp.Core.Compare;
using FerretSharp.Core.Data;
using FerretSharp.Core.Oracle;
using FerretSharp.Core.Query;
using FerretSharp.Core.Resources;
using FerretSharp.Core.Schema;

namespace FerretSharp.Core.Design;

/// <summary>A column of the designer's draft (WP-23, stage 2).</summary>
/// <param name="Original">The column's dictionary name in the table; null for a new column.</param>
/// <param name="Column">The column as it should be – its name may differ from <paramref name="Original"/> (renamed).</param>
/// <param name="Dropped">An existing column the user deleted.</param>
public sealed record ColumnDraft(string? Original, ColumnInfo Column, bool Dropped = false)
{
    public bool IsNew => Original is null;
}

/// <summary>The designer's draft of an existing table: its columns as they should be, in order.</summary>
public sealed record TableDraft(IReadOnlyList<ColumnDraft> Columns)
{
    /// <summary>The table as it is: nothing changed yet.</summary>
    public static TableDraft Of(TableDetails table)
    {
        ArgumentNullException.ThrowIfNull(table);
        return new(table.Columns.Select(c => new ColumnDraft(c.Name, c)).ToList());
    }
}

public enum DataQuestionKind
{
    /// <summary>Rows of the table: a new NOT NULL column without default needs none (ORA-01758).</summary>
    Rows,

    /// <summary>Rows with NULL in the column: NOT NULL and DEFAULT ON NULL need none (ORA-02296).</summary>
    Nulls,

    /// <summary>Rows with a value in the column: some type changes need none (ORA-01439, ORA-01440, ORA-30082).</summary>
    Values,
}

/// <summary>What the designer needs to know about the data before it can say whether a change works.</summary>
/// <param name="Column">The column's current dictionary name; null for <see cref="DataQuestionKind.Rows"/>.</param>
public sealed record DataQuestion(DataQuestionKind Kind, string? Column = null);

/// <summary>The answers to <see cref="DataQuestion"/>s; a missing one is unknown (not counted, or counting failed).</summary>
public sealed class DataFacts(IReadOnlyDictionary<DataQuestion, long> counts)
{
    public static readonly DataFacts None = new(new Dictionary<DataQuestion, long>());

    public long? Of(DataQuestion question) => counts.TryGetValue(question, out var count) ? count : null;
}

/// <summary>The DDL of a draft and what speaks against it.</summary>
/// <param name="Statements">One statement each, in the order they must run; empty if nothing changed.</param>
/// <param name="Problems">Reasons it cannot run (or would certainly fail); empty if it can.</param>
/// <param name="Warnings">Worth knowing before running, no reason to stop.</param>
public sealed record DesignPlan(IReadOnlyList<string> Statements, IReadOnlyList<string> Problems, IReadOnlyList<string> Warnings)
{
    public bool CanRun => Statements.Count > 0 && Problems.Count == 0;
}

/// <summary>
/// The column designer for an existing table (WP-23, stage 2): turns a <see cref="TableDraft"/> into the DDL that makes
/// the table look like it, one statement per change – drops, renames, type, default and NULL changes, new columns,
/// comments, in that order – and finds what Oracle would refuse (experiment against Oracle 23, see
/// <see cref="OracleTypes.ChangeOf"/>). Unlike the DDL proposal of the schema comparison it knows the intent: a renamed or
/// deleted column is meant so and gets real DDL, not a hint. Pure logic; facts about the data come from the caller.
/// </summary>
public static partial class TableDesign
{
    /// <summary>The counts the plan needs to decide about NOT NULL, new mandatory columns and type changes.</summary>
    public static IReadOnlyList<DataQuestion> Questions(TableDetails current, TableDraft draft)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(draft);
        var questions = new List<DataQuestion>();
        foreach (var column in draft.Columns.Where(c => !c.Dropped))
        {
            var after = column.Column;
            if (column.IsNew)
            {
                if (NeedsRowsCheck(after))
                {
                    questions.Add(new DataQuestion(DataQuestionKind.Rows));
                }

                continue;
            }

            if (Find(current, column.Original!) is not { } before || before.IsVirtual || before.IsIdentity)
            {
                continue;
            }

            if (OracleTypes.ChangeOf(before, after) == ColumnTypeChange.NeedsEmptyColumn)
            {
                questions.Add(new DataQuestion(DataQuestionKind.Values, before.Name));
            }

            if (before.Nullable && (!after.Nullable || after.DefaultOnNull) && !before.DefaultOnNull)
            {
                questions.Add(new DataQuestion(DataQuestionKind.Nulls, before.Name));
            }
        }

        return questions.Distinct().ToList();
    }

    /// <summary>
    /// Answers the questions with counts through the read path (<see cref="IDataAccess.CountAsync"/> with IS [NOT] NULL
    /// filters – no SQL of its own), one after the other in the session of <paramref name="data"/>.
    /// </summary>
    public static async Task<DataFacts> CountAsync(IDataAccess data, TableDetails table, IReadOnlyList<DataQuestion> questions, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(questions);
        var counts = new Dictionary<DataQuestion, long>();
        foreach (var question in questions.Distinct())
        {
            IReadOnlyList<FilterCondition> filters = question switch
            {
                { Kind: DataQuestionKind.Nulls, Column: { } column } => [new FilterCondition(column, FilterOperator.IsNull, [])],
                { Kind: DataQuestionKind.Values, Column: { } column } => [new FilterCondition(column, FilterOperator.IsNotNull, [])],
                _ => [],
            };
            counts[question] = await data.CountAsync(table, filters, cancellationToken);
        }

        return new DataFacts(counts);
    }

    /// <param name="constraints">The table's constraints (for columns in keys and checks).</param>
    /// <param name="incoming">Foreign keys of other tables that reference this one.</param>
    public static DesignPlan Plan(TableDetails current, IReadOnlyList<ConstraintInfo> constraints, IReadOnlyList<ForeignKeyInfo> incoming,
        TableDraft draft, DataFacts facts)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(facts);
        var writer = new Writer(current, constraints, incoming, facts);
        writer.Write(draft);
        return new DesignPlan(writer.Statements(), writer.Problems.Distinct().ToList(), writer.Warnings.Distinct().ToList());
    }

    /// <summary>A new NOT NULL column without a value for the existing rows.</summary>
    private static bool NeedsRowsCheck(ColumnInfo column) =>
        !column.Nullable && column.Default is null && !column.IsIdentity && !column.IsVirtual && !column.DefaultOnNull;

    private static ColumnInfo? Find(TableDetails table, string name) => table.IndexOf(name) is var i and >= 0 ? table.Columns[i] : null;

    /// <summary>Dictionary defaults keep what was typed, often with a trailing blank or line break.</summary>
    private static string? Default(ColumnInfo column) => string.IsNullOrWhiteSpace(column.Default) ? null : column.Default.Trim();

    /// <summary>Oracle stores an empty comment as none.</summary>
    private static string? Comment(ColumnInfo column) => string.IsNullOrEmpty(column.Comment) ? null : column.Comment;

    /// <summary>A name Oracle takes without quotes (same rule as for index names).</summary>
    [GeneratedRegex(@"^\p{Lu}[\p{Lu}\p{Nd}_$#]*$")]
    private static partial Regex PlainName();

    private sealed class Writer(TableDetails current, IReadOnlyList<ConstraintInfo> constraints, IReadOnlyList<ForeignKeyInfo> incoming, DataFacts facts)
    {
        private readonly List<string> _drops = [];
        private readonly List<string> _renames = [];
        private readonly List<string> _modifies = [];
        private readonly List<string> _adds = [];
        private readonly List<string> _comments = [];

        public List<string> Problems { get; } = [];

        public List<string> Warnings { get; } = [];

        private string Table => OracleIdentifier.Qualify(current.Table.Owner, current.Table.Name);

        public IReadOnlyList<string> Statements() => [.. _drops, .. _renames, .. _modifies, .. _adds, .. _comments];

        public void Write(TableDraft draft)
        {
            var kept = draft.Columns.Where(c => !c.Dropped).ToList();
            if (kept.Count == 0)
            {
                Problems.Add(DesignText.TableNeedsAColumn);
            }

            CheckNames(kept);
            foreach (var column in draft.Columns)
            {
                if (column.IsNew)
                {
                    if (!column.Dropped)
                    {
                        Add(column.Column);
                    }
                }
                else if (Find(current, column.Original!) is { } before)
                {
                    if (column.Dropped)
                    {
                        Drop(before);
                    }
                    else
                    {
                        Change(before, column.Column);
                    }
                }
            }
        }

        private void CheckNames(IReadOnlyList<ColumnDraft> kept)
        {
            foreach (var column in kept)
            {
                var name = column.Column.Name;
                if (string.IsNullOrWhiteSpace(name))
                {
                    Problems.Add(DesignText.ColumnNameMissing);
                }
                else if (name.Contains('"', StringComparison.Ordinal))
                {
                    Problems.Add(TextFormat.Format(DesignText.ColumnNameInvalid, name));
                }
                else if (Encoding.UTF8.GetByteCount(name) > IndexDesign.MaxNameBytes)
                {
                    Problems.Add(TextFormat.Format(DesignText.ColumnNameTooLong, name, IndexDesign.MaxNameBytes));
                }
                else if (!PlainName().IsMatch(name) && (column.IsNew || name != column.Original))
                {
                    Warnings.Add(TextFormat.Format(DesignText.ColumnNameNeedsQuotes, name));
                }
            }

            foreach (var twice in kept.GroupBy(c => c.Column.Name, StringComparer.Ordinal).Where(g => g.Key.Length > 0 && g.Count() > 1))
            {
                Problems.Add(TextFormat.Format(DesignText.ColumnNameTwice, twice.Key)); // ORA-00957
            }
        }

        /// <summary>Length, precision and scale as Oracle takes them; the type name itself comes from the designer's list.</summary>
        private bool CheckType(ColumnInfo column)
        {
            if (string.IsNullOrWhiteSpace(column.DataType))
            {
                Problems.Add(TextFormat.Format(DesignText.ColumnTypeMissing, column.Name));
                return false;
            }

            if (OracleTypes.HasLength(column.DataType) && column.Length is not > 0)
            {
                Problems.Add(TextFormat.Format(DesignText.ColumnLengthMissing, column.Name, column.DataType));
                return false;
            }

            if (column.DataType == "NUMBER" && (column.Precision is < 1 or > 38 || column.Scale is < -84 or > 127))
            {
                Problems.Add(TextFormat.Format(DesignText.ColumnNumberFacets, column.Name));
                return false;
            }

            return true;
        }

        private void Add(ColumnInfo column)
        {
            if (string.IsNullOrWhiteSpace(column.Name) || column.Name.Contains('"', StringComparison.Ordinal) || !CheckType(column))
            {
                return; // the name is reported with the names
            }

            if (NeedsRowsCheck(column))
            {
                switch (facts.Of(new DataQuestion(DataQuestionKind.Rows)))
                {
                    case > 0 and var rows:
                        Problems.Add(TextFormat.Format(DesignText.AddNotNullNeedsDefault, column.Name, rows)); // ORA-01758
                        break;
                    case null:
                        Warnings.Add(TextFormat.Format(DesignText.AddNotNullUnchecked, column.Name));
                        break;
                }
            }

            _adds.Add($"ALTER TABLE {Table} ADD ({DdlText.ColumnDefinition(column with { Default = Default(column) })})");
            if (Comment(column) is { } comment)
            {
                _comments.Add(CommentOn(column.Name, comment));
            }
        }

        private void Drop(ColumnInfo column)
        {
            // Oracle drops single-column constraints with the column; a key others reference or a constraint over several
            // columns it refuses (WP-23 experiment).
            var referencing = incoming.Where(fk => fk.ToColumns.Contains(column.Name, StringComparer.Ordinal)).Select(fk => fk.Name).ToList();
            if (referencing.Count > 0)
            {
                Problems.Add(TextFormat.Format(DesignText.DropReferencedColumn, column.Name, string.Join(", ", referencing))); // ORA-12992
            }

            foreach (var constraint in constraints.Where(c => c.Columns.Count > 1 && c.Columns.Contains(column.Name, StringComparer.Ordinal)))
            {
                Problems.Add(TextFormat.Format(DesignText.DropColumnInConstraint, column.Name, constraint.Name)); // ORA-12991
            }

            Warnings.Add(TextFormat.Format(DesignText.DropColumnLosesData, column.Name));
            _drops.Add($"ALTER TABLE {Table} DROP COLUMN {OracleIdentifier.Quote(column.Name)}");
        }

        private void Change(ColumnInfo before, ColumnInfo after)
        {
            var name = after.Name;
            if (string.IsNullOrWhiteSpace(name) || name.Contains('"', StringComparison.Ordinal))
            {
                return; // reported with the names
            }

            if (!string.Equals(before.Name, name, StringComparison.Ordinal))
            {
                _renames.Add($"ALTER TABLE {Table} RENAME COLUMN {OracleIdentifier.Quote(before.Name)} TO {OracleIdentifier.Quote(name)}");
                Warnings.Add(TextFormat.Format(DesignText.RenameKeepsReferences, before.Name, name));
            }

            if (!string.Equals(Comment(before), Comment(after), StringComparison.Ordinal))
            {
                _comments.Add(CommentOn(name, Comment(after)));
            }

            // Virtual and identity columns: their definition is not the designer's (the type of an identity column, its
            // generation, a virtual expression); renaming and comments are.
            var changed = OracleTypes.ChangeOf(before, after) != ColumnTypeChange.Same
                          || before.Nullable != after.Nullable || before.DefaultOnNull != after.DefaultOnNull
                          || !string.Equals(Default(before), Default(after), StringComparison.Ordinal);
            if (before.IsVirtual || before.IsIdentity || after.IsIdentity || after.IsVirtual)
            {
                if (changed || before.IsIdentity != after.IsIdentity || before.IsVirtual != after.IsVirtual)
                {
                    Problems.Add(TextFormat.Format(before.IsIdentity || after.IsIdentity ? DesignText.IdentityOnlyNew : DesignText.VirtualNotChanged, name));
                }

                return;
            }

            if (!CheckType(after))
            {
                return;
            }

            var column = OracleIdentifier.Quote(name);
            ChangeType(before, after, column);
            ChangeDefault(before, after, column);
            ChangeNull(before, after, column);
        }

        private void ChangeType(ColumnInfo before, ColumnInfo after, string column)
        {
            var change = OracleTypes.ChangeOf(before, after);
            var from = OracleTypes.DisplayType(before);
            var to = OracleTypes.DisplayType(after);
            switch (change)
            {
                case ColumnTypeChange.Same:
                    return;
                case ColumnTypeChange.NotPossible:
                    Problems.Add(TextFormat.Format(DesignText.TypeChangeNotPossible, after.Name, from, to));
                    return;
                case ColumnTypeChange.MayFailOnValues:
                    Warnings.Add(TextFormat.Format(DesignText.TypeChangeMayFail, after.Name, from, to));
                    break;
                case ColumnTypeChange.NeedsEmptyColumn:
                    switch (facts.Of(new DataQuestion(DataQuestionKind.Values, before.Name)))
                    {
                        case > 0 and var values:
                            Problems.Add(TextFormat.Format(DesignText.TypeChangeNeedsEmpty, after.Name, from, to, values));
                            return;
                        case null:
                            Warnings.Add(TextFormat.Format(DesignText.TypeChangeNeedsEmptyUnchecked, after.Name, from, to));
                            break;
                    }

                    break;
            }

            _modifies.Add($"ALTER TABLE {Table} MODIFY ({column} {OracleTypes.DdlType(after)})");
        }

        private void ChangeDefault(ColumnInfo before, ColumnInfo after, string column)
        {
            var value = Default(after);
            if (before.DefaultOnNull == after.DefaultOnNull && string.Equals(Default(before), value, StringComparison.Ordinal))
            {
                return;
            }

            if (after.DefaultOnNull && value is null)
            {
                Problems.Add(TextFormat.Format(DesignText.DefaultOnNullNeedsValue, after.Name));
                return;
            }

            if (after.DefaultOnNull && before.Nullable && !before.DefaultOnNull && !CheckNoNulls(before, after.Name))
            {
                return;
            }

            _modifies.Add($"ALTER TABLE {Table} MODIFY ({column} DEFAULT {(after.DefaultOnNull ? "ON NULL " : "")}{value ?? "NULL"})");

            // Taking ON NULL away takes NOT NULL with it (Oracle 23, WP-20): keep the column NOT NULL if it should stay so.
            if (before.DefaultOnNull && !after.DefaultOnNull && !after.Nullable)
            {
                _modifies.Add($"ALTER TABLE {Table} MODIFY ({column} NOT NULL)");
            }
        }

        private void ChangeNull(ColumnInfo before, ColumnInfo after, string column)
        {
            // DEFAULT ON NULL brings NOT NULL along and takes it away again: handled with the default.
            if (before.DefaultOnNull || after.DefaultOnNull || before.Nullable == after.Nullable)
            {
                return;
            }

            if (after.Nullable)
            {
                if (current.PrimaryKey.Contains(before.Name, StringComparer.Ordinal))
                {
                    Problems.Add(TextFormat.Format(DesignText.NullOnPrimaryKey, after.Name)); // ORA-01451
                    return;
                }

                _modifies.Add($"ALTER TABLE {Table} MODIFY ({column} NULL)");
            }
            else if (CheckNoNulls(before, after.Name))
            {
                _modifies.Add($"ALTER TABLE {Table} MODIFY ({column} NOT NULL)");
            }
        }

        /// <summary>NOT NULL and DEFAULT ON NULL need a column without NULLs (ORA-02296).</summary>
        private bool CheckNoNulls(ColumnInfo before, string name)
        {
            switch (facts.Of(new DataQuestion(DataQuestionKind.Nulls, before.Name)))
            {
                case > 0 and var nulls:
                    Problems.Add(TextFormat.Format(DesignText.NotNullHasNulls, name, nulls));
                    return false;
                case null:
                    Warnings.Add(TextFormat.Format(DesignText.NotNullUnchecked, name));
                    return true;
                default:
                    return true;
            }
        }

        private string CommentOn(string column, string? comment) =>
            $"COMMENT ON COLUMN {Table}.{OracleIdentifier.Quote(column)} IS {DdlText.Literal(comment)}";
    }
}
