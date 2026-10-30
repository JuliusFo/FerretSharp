using System.Text;
using System.Text.RegularExpressions;
using FerretSharp.Core.Data;
using FerretSharp.Core.Ddl;
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

/// <summary>A constraint the designer adds (WP-23, stage 2).</summary>
/// <param name="Name">Its name in dictionary form; empty lets Oracle name it (<c>SYS_C…</c>).</param>
/// <param name="Columns">Columns of the draft (their new names), in key order.</param>
/// <param name="Condition">The condition of a check.</param>
/// <param name="References">The table a foreign key references.</param>
/// <param name="ReferencedColumns">Its key columns, in the order of <paramref name="Columns"/>.</param>
/// <param name="DeleteRule">A foreign key's <c>CASCADE</c> or <c>SET NULL</c>; null for none (NO ACTION).</param>
/// <param name="WithIndex">A foreign key gets an index on its columns too, unless one covers them already.</param>
public sealed record ConstraintDraft(
    string Name,
    ConstraintType Type,
    IReadOnlyList<string> Columns,
    string? Condition = null,
    TableRef? References = null,
    IReadOnlyList<string>? ReferencedColumns = null,
    string? DeleteRule = null,
    bool WithIndex = true);

/// <summary>An existing constraint the designer renames or changes, as it should be.</summary>
/// <param name="Original">Its name in the table.</param>
/// <param name="Name">The name it should have, in dictionary form.</param>
/// <param name="Condition">A check's condition (the columns by their drafted names); null for another constraint.</param>
/// <param name="DeleteRule">A foreign key's <c>CASCADE</c> or <c>SET NULL</c>; null for none (NO ACTION).</param>
/// <param name="RenameIndex">A renamed key's index of the key's name is renamed with it.</param>
public sealed record ConstraintEdit(string Original, string Name, string? Condition = null, string? DeleteRule = null, bool RenameIndex = true);

/// <summary>The designer's draft of an existing table: its columns as they should be, in order, and its constraint changes.</summary>
public sealed record TableDraft(IReadOnlyList<ColumnDraft> Columns)
{
    /// <summary>Names of existing constraints to drop.</summary>
    public IReadOnlyList<string> DroppedConstraints { get; init; } = [];

    /// <summary>New constraints.</summary>
    public IReadOnlyList<ConstraintDraft> AddedConstraints { get; init; } = [];

    /// <summary>Existing constraints renamed or changed (a check's condition, a foreign key's delete rule).</summary>
    public IReadOnlyList<ConstraintEdit> ChangedConstraints { get; init; } = [];

    /// <summary>
    /// The tablespace of every index this draft creates (WP-67, dictionary form): those of new primary and unique keys and
    /// of new foreign keys. Null for the owner's default.
    /// </summary>
    public string? IndexTablespace { get; init; }

    /// <summary>The table as it is: nothing changed yet.</summary>
    public static TableDraft Of(TableDetails table)
    {
        ArgumentNullException.ThrowIfNull(table);
        return new(table.Columns.Select(c => new ColumnDraft(c.Name, c)).ToList());
    }
}

/// <summary>A table the designer creates (WP-23, stage 3).</summary>
/// <param name="Name">Its name in dictionary form.</param>
/// <param name="Comment">The table's comment; null or empty for none.</param>
/// <param name="Tablespace">The tablespace to create it in (WP-67, dictionary form); null for the owner's default.</param>
public sealed record NewTable(string Owner, string Name, string? Comment = null, string? Tablespace = null)
{
    public TableRef Ref => new(Owner, Name);
}

/// <summary>What the designer knows about the table besides its columns.</summary>
/// <param name="Constraints">The table's constraints (keys, checks, foreign keys).</param>
/// <param name="Indexes">The table's indexes (does a new foreign key need one?).</param>
/// <param name="Incoming">Foreign keys of other tables that reference this one.</param>
public sealed record DesignContext(TableDetails Table, IReadOnlyList<ConstraintInfo> Constraints, IReadOnlyList<IndexInfo> Indexes, IReadOnlyList<ForeignKeyInfo> Incoming)
{
    /// <summary>The tables new foreign keys reference, for their keys and column types; a missing one is not checked.</summary>
    public IReadOnlyDictionary<TableRef, TableDetails> Referenced { get; init; } = new Dictionary<TableRef, TableDetails>();

    /// <summary>Names other tables of the owner hold (review 3.54.0, A14); none known by default.</summary>
    public SchemaNames Elsewhere { get; init; } = SchemaNames.None;

    /// <summary>The owner's quotas for a chosen tablespace (WP-67); none known by default.</summary>
    public Tablespaces Tablespaces { get; init; } = Tablespaces.None;

    /// <summary>Only the table: no constraints, indexes or references known.</summary>
    public static DesignContext Of(TableDetails table) => new(table, [], [], []);
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
/// <param name="Steps">One statement each, in the order they must run (<see cref="DdlScript.Order"/>); empty if nothing changed.</param>
/// <param name="Problems">Reasons it cannot run (or would certainly fail); empty if it can.</param>
/// <param name="Warnings">Worth knowing before running, no reason to stop – the steps carry none themselves.</param>
public sealed record DesignPlan(IReadOnlyList<DdlStep> Steps, IReadOnlyList<string> Problems, IReadOnlyList<string> Warnings)
{
    /// <summary>The statements of <see cref="Steps"/>, without semicolon.</summary>
    public IReadOnlyList<string> Statements { get; } = Steps.Select(s => s.Sql).ToList();

    public bool CanRun => Runnable(Steps, Problems);

    /// <summary>Steps to run and nothing against them – also for a plan put together from several (the designer with its audit).</summary>
    public static bool Runnable(IReadOnlyCollection<DdlStep> steps, IReadOnlyCollection<string> problems) => steps.Count > 0 && problems.Count == 0;

    /// <summary>The <see cref="Problems"/> about constraints (the designer marks its constraints tab); the rest are the columns'.</summary>
    public IReadOnlyList<string> ConstraintProblems { get; init; } = [];

    /// <summary>
    /// The indexes the plan creates (WP-67): those of new keys no index of the table backs, and those of new foreign keys –
    /// where <see cref="TableDraft.IndexTablespace"/> applies. In the order of the constraints.
    /// </summary>
    public IReadOnlyList<NewIndex> NewIndexes { get; init; } = [];
}

/// <summary>An index a design plan creates.</summary>
/// <param name="Name">Its name – a new key's index is named after the key (its kind and columns while Oracle names it).</param>
/// <param name="ForeignKey">The foreign key it is for; null for the index of a key.</param>
public sealed record NewIndex(string Name, string? ForeignKey = null);

/// <summary>
/// The column designer for an existing table (WP-23, stage 2): turns a <see cref="TableDraft"/> into the DDL that makes
/// the table look like it, one statement per change in the order of <see cref="DdlScript.Order"/> – drops, renames, type,
/// default and NULL changes, new columns, constraints, indexes, comments – and finds what Oracle would refuse (experiment
/// against Oracle 23, see <see cref="OracleTypes.ChangeOf"/>). Unlike the DDL proposal of the schema comparison it knows
/// the intent (<see cref="ChangeIntent.Known"/>): a renamed or deleted column is meant so and gets real DDL, not a hint.
/// Pure logic; facts about the data come from the caller.
/// </summary>
public static partial class TableDesign
{
    /// <summary>The counts the plan needs to decide about NOT NULL, new mandatory columns and type changes.</summary>
    /// <param name="constraints">
    /// The table's constraints: only an edit that checks the rows again (<see cref="Rechecks"/>) needs them counted, a rename
    /// does not. Without them every edit counts as one.
    /// </param>
    public static IReadOnlyList<DataQuestion> Questions(TableDetails current, TableDraft draft, IReadOnlyList<ConstraintInfo>? constraints = null)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(draft);
        var questions = new List<DataQuestion>();
        foreach (var column in draft.Columns.Where(c => !c.Dropped))
        {
            var after = column.Column;
            if (column.IsNew)
            {
                if (ColumnChange.NeedsEmptyTable(after))
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

        var rechecking = draft.ChangedConstraints.Any(e => !draft.DroppedConstraints.Contains(e.Original)
            && (constraints is null || (constraints.FirstOrDefault(c => c.Name == e.Original) is { } before && Rechecks(before, e))));
        if (draft.AddedConstraints.Count > 0 || rechecking)
        {
            questions.Add(new DataQuestion(DataQuestionKind.Rows)); // an empty table has no rows to break a new or changed constraint
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

    /// <summary>The DDL for the draft, and what speaks against it.</summary>
    public static DesignPlan Plan(DesignContext context, TableDraft draft, DataFacts facts)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(facts);
        var writer = new Writer(context, facts);
        writer.Write(draft);
        return writer.Result();
    }

    /// <summary>
    /// The DDL for a new table (WP-23, stage 3): <c>CREATE TABLE</c> with every column of the draft, then its constraints
    /// (foreign keys with their index), the table's and the columns' comments – the same writer as for an existing table.
    /// </summary>
    /// <param name="referenced">Tables the new foreign keys reference, for their keys and column types.</param>
    /// <param name="taken">Names of the schema's objects (tables, views, synonyms …): one of them cannot be the new table's (ORA-00955).</param>
    /// <param name="tablespaces">The owner's quotas for the chosen tablespaces (WP-67); null while not read.</param>
    public static DesignPlan PlanNew(NewTable table, TableDraft draft, IReadOnlyDictionary<TableRef, TableDetails> referenced, IEnumerable<string> taken,
        Tablespaces? tablespaces = null)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(draft);
        var empty = new TableDetails(new TableSummary(table.Owner, table.Name, TableKind.Table), [], [], [], false);
        var context = new DesignContext(empty, [], [], []) { Referenced = referenced, Tablespaces = tablespaces ?? Tablespaces.None };
        var writer = new Writer(context, new DataFacts(new Dictionary<DataQuestion, long> { [new DataQuestion(DataQuestionKind.Rows)] = 0 }), table);
        writer.CheckTableName(taken);
        if (writer.Problems.Count == 0)
        {
            writer.Write(draft);
        }

        return writer.Result();
    }

    private static ColumnInfo? Find(TableDetails table, string name) => table.IndexOf(name) is var i and >= 0 ? table.Columns[i] : null;

    /// <summary>Dictionary defaults keep what was typed, often with a trailing blank or line break.</summary>
    private static string? Default(ColumnInfo column) => string.IsNullOrWhiteSpace(column.Default) ? null : column.Default.Trim();

    /// <summary>Oracle stores an empty comment as none.</summary>
    private static string? Comment(string? comment) => string.IsNullOrEmpty(comment) ? null : comment;

    /// <summary>The column as the DDL compares and writes it: default trimmed, an empty default or comment none.</summary>
    private static ColumnInfo Normalized(ColumnInfo column) => column with { Default = Default(column), Comment = Comment(column.Comment) };

    private sealed partial class Writer(DesignContext context, DataFacts facts, NewTable? creating = null)
    {
        private readonly TableDetails current = context.Table;

        /// <summary>Column definitions of the table to create (<see cref="PlanNew"/>).</summary>
        private readonly List<string> _definitions = [];

        private readonly List<DdlStep> _steps = [];

        /// <summary>Renamed columns: written together at the end, in an order that works (<see cref="ColumnChange.Renames"/>).</summary>
        private readonly List<(string From, string To)> _renames = [];

        /// <summary>Existing constraints the draft drops: a column in them may go, a key column may allow NULL.</summary>
        private HashSet<string> _dropped = [];

        public List<string> Problems { get; } = [];

        /// <summary>The problems found while dropping, changing and adding constraints.</summary>
        private readonly List<string> _constraintProblems = [];

        public List<string> Warnings { get; } = [];

        private TableRef Table => current.Table.Ref;

        private IReadOnlyList<DdlStep> Steps() =>
            DdlScript.Order([.. _steps, .. ColumnChange.Renames(Table, _renames, current.Columns.Select(c => c.Name))]);

        /// <summary>Takes steps over; the designer words its own warnings (with counted data), so theirs go.</summary>
        private void Keep(IEnumerable<DdlStep> steps) => _steps.AddRange(steps.Select(s => s with { Warning = null }));

        private void Keep(DdlStepKind kind, string sql) => _steps.Add(new DdlStep(current.Table.Name, kind, sql));

        public void Write(TableDraft draft)
        {
            var kept = draft.Columns.Where(c => !c.Dropped).ToList();
            if (kept.Count == 0)
            {
                Problems.Add(DesignText.TableNeedsAColumn);
            }

            CheckNames(kept);
            var problems = Problems.Count;
            DropConstraints(draft.DroppedConstraints);
            EditConstraints(draft);
            _constraintProblems.AddRange(Problems.Skip(problems));
            if (creating is not null && Comment(creating.Comment) is { } tableComment)
            {
                Keep(DdlStepKind.Comment, OracleDdl.CommentOnTable(Table, tableComment));
            }

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

            var tablespace = Usable(creating?.Tablespace);
            if (creating is not null && _definitions.Count > 0)
            {
                Keep(DdlStepKind.CreateTable, OracleDdl.CreateTable(Table, _definitions, tablespace: tablespace));
            }

            IndexDesign.CheckTablespace(tablespace, Table.Owner, context.Tablespaces, Problems, Warnings);

            problems = Problems.Count;
            AddConstraints(draft, kept);
            _constraintProblems.AddRange(Problems.Skip(problems));
        }

        /// <summary>The name of a table to create: there, without quotes, within 128 bytes, not taken by another object.</summary>
        public void CheckTableName(IEnumerable<string> taken)
        {
            var name = creating!.Name;
            if (string.IsNullOrWhiteSpace(name))
            {
                Problems.Add(DesignText.TableNameMissing);
            }
            else if (OracleIdentifier.Problem(name) is { } problem)
            {
                Problems.Add(problem);
            }
            else if (taken.Contains(name, StringComparer.Ordinal))
            {
                Problems.Add(TextFormat.Format(DesignText.TableNameTaken, name)); // ORA-00955
            }
            else if (OracleIdentifier.NeedsQuotes(name))
            {
                Warnings.Add(TextFormat.Format(DesignText.NameNeedsQuotes, name));
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
                else if (OracleIdentifier.Problem(name) is { } problem)
                {
                    Problems.Add(problem);
                }
                else if (OracleIdentifier.NeedsQuotes(name) && (column.IsNew || name != column.Original))
                {
                    Warnings.Add(TextFormat.Format(DesignText.NameNeedsQuotes, name));
                }
            }

            foreach (var twice in kept.GroupBy(c => c.Column.Name, StringComparer.Ordinal).Where(g => g.Key.Length > 0 && g.Count() > 1))
            {
                Problems.Add(TextFormat.Format(DesignText.ColumnNameTwice, twice.Key)); // ORA-00957
            }
        }

        /// <summary>
        /// The plan. A statement the schema path would refuse (<see cref="StatementGuard.IsDdl"/>) is a problem here (R4a),
        /// not an error halfway through the run, after the statements before it were committed.
        /// </summary>
        public DesignPlan Result()
        {
            var steps = Steps();
            if (Problems.Count == 0)
            {
                Problems.AddRange(steps.Select(s => s.Sql).Where(s => !StatementGuard.IsDdl(s))
                    .Select(s => TextFormat.Format(DesignText.StatementNotDdl, s.Length <= 80 ? s : s[..79] + "…")));
            }

            return new DesignPlan(steps, Problems.Distinct().ToList(), Warnings.Distinct().ToList())
            {
                ConstraintProblems = _constraintProblems.Distinct().ToList(),
                NewIndexes = _newIndexes,
            };
        }

        /// <summary>
        /// A typed expression (default, check condition) that fits into one DDL statement (<see cref="OracleDdl.FitsInStatement"/>);
        /// a problem otherwise. A <c>;</c> at its end is easily typed.
        /// </summary>
        private bool CheckExpression(string label, string? expression)
        {
            if (expression is null || OracleDdl.FitsInStatement(expression))
            {
                return true;
            }

            Problems.Add(TextFormat.Format(DesignText.ExpressionNotInStatement, label));
            return false;
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

            // The size as the designer's field has it: a length, a NUMBER's or FLOAT's precision.
            var size = OracleTypes.HasLength(column.DataType) ? column.Length : column.DataType is "NUMBER" or "FLOAT" ? column.Precision : null;
            if (size is { } s && OracleTypes.SizeRange(column.DataType) is { } range && !OracleTypes.Within(range, s))
            {
                Problems.Add(TextFormat.Format(DesignText.ColumnSizeRange, column.Name, s, column.DataType, range.Min, range.Max));
                return false;
            }

            if (OracleTypes.IsNumber(column.DataType) && column.Scale is { } scale && !OracleTypes.Within(OracleTypes.ScaleRange, scale))
            {
                Problems.Add(TextFormat.Format(DesignText.ColumnScaleRange, column.Name, scale, OracleTypes.ScaleRange.Min, OracleTypes.ScaleRange.Max));
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

            if (!CheckExpression(column.Name, Default(column)))
            {
                return;
            }

            if (ColumnChange.NeedsEmptyTable(column))
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

            var added = Normalized(column);
            if (creating is null)
            {
                Keep(ColumnChange.Steps(Table, null, added, ChangeIntent.Known));
                return;
            }

            _definitions.Add(OracleDdl.ColumnDefinition(added));
            if (added.Comment is { } comment)
            {
                Keep(DdlStepKind.Comment, OracleDdl.CommentOnColumn(Table, added.Name, comment));
            }
        }

        private void Drop(ColumnInfo column)
        {
            // Oracle drops single-column constraints with the column; a key others reference or a constraint over several
            // columns it refuses (WP-23 experiment).
            var referencing = context.Incoming.Where(fk => fk.ToColumns.Contains(column.Name, StringComparer.Ordinal)).Select(fk => fk.Name).ToList();
            if (referencing.Count > 0)
            {
                Problems.Add(TextFormat.Format(DesignText.DropReferencedColumn, column.Name, string.Join(", ", referencing))); // ORA-12992
            }

            foreach (var constraint in context.Constraints.Where(c => c.Columns.Count > 1 && c.Columns.Contains(column.Name, StringComparer.Ordinal) && !_dropped.Contains(c.Name)))
            {
                Problems.Add(TextFormat.Format(DesignText.DropColumnInConstraint, column.Name, constraint.Name)); // ORA-12991
            }

            // Oracle drops every index on the column without a word, also one over several columns (R4a).
            foreach (var index in context.Indexes.Where(i => i.Columns.Count > 1 && i.Columns.Any(c => !c.IsExpression && c.Name == column.Name)))
            {
                Warnings.Add(TextFormat.Format(DesignText.DropColumnDropsIndex, column.Name, index.Name));
            }

            Warnings.Add(TextFormat.Format(DesignText.DropColumnLosesData, column.Name));
            Keep(ColumnChange.Steps(Table, column, null, ChangeIntent.Known));
        }

        /// <summary>
        /// The steps <see cref="ColumnChange"/> writes for the column, each checked against the table and the counted data:
        /// one Oracle would refuse becomes a problem instead (and is left out).
        /// </summary>
        private void Change(ColumnInfo before, ColumnInfo after)
        {
            var name = after.Name;
            if (string.IsNullOrWhiteSpace(name) || name.Contains('"', StringComparison.Ordinal))
            {
                return; // reported with the names
            }

            if (!string.Equals(before.Name, name, StringComparison.Ordinal))
            {
                _renames.Add((before.Name, name));
                Warnings.Add(TextFormat.Format(DesignText.RenameKeepsReferences, before.Name, name));
            }

            var steps = ColumnChange.Steps(Table, Normalized(before), Normalized(after), ChangeIntent.Known).Where(s => s.Kind != DdlStepKind.RenameColumn).ToList();
            Keep(steps.Where(s => s.Kind == DdlStepKind.Comment));

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

            var defaultTaken = true;
            foreach (var step in steps)
            {
                var take = step.Kind switch
                {
                    DdlStepKind.ModifyType => TypeChange(before, after),
                    DdlStepKind.ModifyDefault => defaultTaken = DefaultChange(before, after),
                    // After DEFAULT ON NULL was taken away: NOT NULL again, together with the default step.
                    DdlStepKind.ModifyNull when before.DefaultOnNull => defaultTaken,
                    DdlStepKind.ModifyNull => NullChange(before, after),
                    _ => false, // the comment is kept already
                };
                if (take)
                {
                    Keep([step]);
                }
            }
        }

        private bool TypeChange(ColumnInfo before, ColumnInfo after)
        {
            var from = OracleTypes.DisplayType(before);
            var to = OracleTypes.DisplayType(after);
            switch (OracleTypes.ChangeOf(before, after))
            {
                case ColumnTypeChange.NotPossible:
                    Problems.Add(TextFormat.Format(DesignText.TypeChangeNotPossible, after.Name, from, to));
                    return false;
                case ColumnTypeChange.MayFailOnValues:
                    Warnings.Add(TextFormat.Format(DesignText.TypeChangeMayFail, after.Name, from, to));
                    if (OracleTypes.ValueChangeOf(before, after) == ValueChange.PadsWithBlanks)
                    {
                        Warnings.Add(TextFormat.Format(DesignText.TypeChangePads, after.Name, from, to)); // what fits (review 3.44.0, A13)
                    }

                    return true;
                case ColumnTypeChange.ChangesValues: // review 3.40.0, A9: went through without a word
                    Warnings.Add(TextFormat.Format(OracleTypes.ValueChangeOf(before, after) switch
                    {
                        ValueChange.CutsFractions => DesignText.TypeChangeCutsFractions,
                        ValueChange.KeepsBlanks => DesignText.TypeChangeKeepsBlanks, // review 3.44.0, A7
                        _ => DesignText.TypeChangePads,
                    }, after.Name, from, to));
                    return true;
                case ColumnTypeChange.NeedsEmptyColumn:
                    switch (facts.Of(new DataQuestion(DataQuestionKind.Values, before.Name)))
                    {
                        case > 0 and var values:
                            Problems.Add(TextFormat.Format(DesignText.TypeChangeNeedsEmpty, after.Name, from, to, values));
                            return false;
                        case null:
                            Warnings.Add(TextFormat.Format(DesignText.TypeChangeNeedsEmptyUnchecked, after.Name, from, to));
                            return true;
                    }

                    return true;
                default:
                    return true;
            }
        }

        private bool DefaultChange(ColumnInfo before, ColumnInfo after)
        {
            var value = Default(after);
            if (after.DefaultOnNull && value is null)
            {
                Problems.Add(TextFormat.Format(DesignText.DefaultOnNullNeedsValue, after.Name));
                return false;
            }

            return CheckExpression(after.Name, value)
                   && !(after.DefaultOnNull && before.Nullable && !before.DefaultOnNull && !CheckNoNulls(before, after.Name));
        }

        private bool NullChange(ColumnInfo before, ColumnInfo after)
        {
            if (!after.Nullable)
            {
                return CheckNoNulls(before, after.Name);
            }

            if (current.PrimaryKey.Contains(before.Name, StringComparer.Ordinal) && !PrimaryKeyDropped)
            {
                Problems.Add(TextFormat.Format(DesignText.NullOnPrimaryKey, after.Name)); // ORA-01451
                return false;
            }

            return true;
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
    }
}
